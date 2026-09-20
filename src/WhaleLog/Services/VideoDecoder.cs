using System.Diagnostics;
using System.IO;
using System.Text;

namespace WhaleLog.Services;

/// <summary>
/// 一段透明 webm 的**流式**解码器：起一个 ffmpeg 子进程把视频逐帧解成 RGBA，
/// 用一个小环形缓冲把帧递给界面。这是 dsh 那 106 个动作能播起来的基础件。
///
/// 为什么是流式，而不是"一次性全解进内存"
/// ------------------------------------
/// 实测过：一个动作 241 帧、格子 384×384 RGBA，**一帧 576 KB**，
/// 整个动作就是 142 MB。106 个动作全解 = 15 GB，单个动作也扛不住
/// （悬浮窗是个常驻后台的小东西，不该占着一百多 MB 不还）。
/// 而同一个素材 ffmpeg 只要 0.38 秒就能解完 —— 比实时快 26 倍。
/// 所以正确做法是**边播边解**：只留几帧在手，解码永远跑在播放前面一点点。
///
/// 为什么不上 -re（让 ffmpeg 自己按实时速率吐帧）
/// ------------------------------------------
/// 那样确实更省事，但节奏就交给 ffmpeg 了，界面卡一下就会积压，
/// 而且"暂停/恢复"没法控制。这里反过来：**ffmpeg 全速解，由环形缓冲的水位卡住它**。
/// 缓冲满了 ffmpeg 就阻塞在写管道上，天然不积压、不吃 CPU（实测解码时 ffmpeg 基本在睡），
/// 播放节奏仍由界面那侧的时间轴说了算。
///
/// 线程模型
/// --------
///   · 读帧线程：从 ffmpeg 的 stdout 读满一帧 → 放进 ready 队列；
///     没有空槽就等（这就是上面说的"水位"）。
///   · stderr 排空线程：ffmpeg 出错时会往 stderr 写，管道写满它就会卡死，
///     所以必须有人读。顺手留最后几行当报错信息。
///   · 调用方（界面线程）TryTake 取帧、用完 Return 还槽。
///
/// 这个类不碰 WPF（给的是 byte[]，不是 BitmapSource），自测能直接编译它。
/// </summary>
public sealed class VideoFrameStream : IDisposable
{
    /// <summary>留几帧在手。8 帧 = 4.6 MB，够盖住"ffmpeg 启动 + 系统抖动"那段。</summary>
    public const int DefaultBufferCount = 8;

    /// <summary>格子边长的允许区间。超出这个范围的帧表是坏的，不该拿它去开进程。</summary>
    public const int MinCellSide = 16;
    public const int MaxCellSide = 2048;

    private readonly Process _process;
    private readonly Thread _reader;
    private readonly Thread _drain;
    private readonly Stream? _stdout;
    private readonly object _gate = new();

    /// <summary>空槽（等读入）。</summary>
    private readonly Queue<byte[]> _free = new();

    /// <summary>已解好、等界面取的帧。</summary>
    private readonly Queue<byte[]> _ready = new();

    private bool _stopped;
    private bool _eof;
    private int _inFlight;
    private string _error = "";
    private readonly StringBuilder _stderrTail = new();

    /// <summary>格子边长（解码后的方形边长）。</summary>
    public int CellSide { get; }

    /// <summary>一帧多少字节。</summary>
    public int FrameBytes { get; }

    /// <summary>环里有几个槽。诊断用。</summary>
    public int BufferCount { get; }

    /// <summary>素材路径（诊断用）。</summary>
    public string VideoPath { get; }

    /// <summary>ffmpeg 解完了（非循环动作播到结尾，或者进程挂了）。</summary>
    public bool IsEof { get { lock (_gate) return _eof; } }

    /// <summary>解完了、手上也没有待取的帧了 —— 这一段到此为止。</summary>
    public bool IsDrained
    {
        get { lock (_gate) return _eof && _ready.Count == 0 && _inFlight == 0; }
    }

    /// <summary>出错原因（ffmpeg 没起来 / 滤镜不认 / 进程中途挂了），正常为空。</summary>
    public string Error
    {
        get
        {
            lock (_gate)
            {
                if (_error.Length > 0) return _error;
                return _stderrTail.Length > 0 ? _stderrTail.ToString().Trim() : "";
            }
        }
    }

    /// <summary>已经解出来、还没被取走的帧数。诊断用。</summary>
    public int ReadyCount { get { lock (_gate) return _ready.Count; } }

    /// <summary>ffmpeg 进程还活着吗。</summary>
    public bool IsRunning
    {
        get
        {
            try { return !_process.HasExited; }
            catch { return false; }
        }
    }

    /// <summary>
    /// 起 ffmpeg 开始解码。构造完就开始读了（也就是"预解码"）——
    /// 悬停时提前建一个，点下去就不用等进程启动那一百多毫秒。
    /// </summary>
    /// <param name="ffmpegExe">ffmpeg 完整路径。</param>
    /// <param name="videoPath">透明 webm 的完整路径。</param>
    /// <param name="filter">把源画布裁进格子的滤镜串（来自帧表，已校验过）。</param>
    /// <param name="cellSide">格子边长，必须和滤镜的输出尺寸一致。</param>
    /// <param name="loop">true 时用 -stream_loop -1 让输入无缝循环（待机、拖拽这类要一直播的）。</param>
    /// <param name="bufferCount">环形缓冲槽数。</param>
    public VideoFrameStream(
        string ffmpegExe, string videoPath, string filter, int cellSide, bool loop,
        int bufferCount = DefaultBufferCount)
    {
        if (string.IsNullOrWhiteSpace(ffmpegExe)) throw new ArgumentException("ffmpeg 路径为空", nameof(ffmpegExe));
        if (string.IsNullOrWhiteSpace(videoPath)) throw new ArgumentException("视频路径为空", nameof(videoPath));
        if (cellSide < MinCellSide || cellSide > MaxCellSide)
            throw new ArgumentOutOfRangeException(nameof(cellSide), $"格子边长 {cellSide} 超出 {MinCellSide}–{MaxCellSide}");

        if (!File.Exists(ffmpegExe)) throw new FileNotFoundException("找不到 ffmpeg", ffmpegExe);
        if (!File.Exists(videoPath)) throw new FileNotFoundException("找不到视频素材", videoPath);

        BufferCount = Math.Clamp(bufferCount, 2, 32);
        CellSide = cellSide;
        FrameBytes = cellSide * cellSide * 4;
        VideoPath = videoPath;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };

        // -nostdin：ffmpeg 默认会去读标准输入，在 GUI 程序里这是找死。
        // -an/-sn/-dn：素材里万一混了音轨/字幕轨，别让它干扰帧的对应关系。
        // rawvideo + rgba：直接给像素，不带任何容器头 —— 每帧定长，按字节数切就行。
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        if (loop)
        {
            psi.ArgumentList.Add("-stream_loop");
            psi.ArgumentList.Add("-1");
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add(filter);
        }
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-sn");
        psi.ArgumentList.Add("-dn");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("rgba");
        psi.ArgumentList.Add("-");

        try
        {
            _process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg 进程没起来");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"启动 ffmpeg 失败：{ex.Message}", ex);
        }

        _stdout = _process.StandardOutput.BaseStream;

        for (int i = 0; i < BufferCount; i++) _free.Enqueue(new byte[FrameBytes]);

        _drain = new Thread(DrainLoop) { IsBackground = true, Name = "whalelog-ffmpeg-err" };
        _drain.Start();

        _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "whalelog-ffmpeg-out" };
        _reader.Start();
    }

    /// <summary>
    /// 取一帧。取到之后**必须**用 <see cref="Return"/> 还回去，否则缓冲槽会用光、
    /// 解码线程就停在那儿了（这也是它"不积压"的原理，但漏还就成了卡死）。
    /// </summary>
    public bool TryTake(out byte[]? frame, int timeoutMs = 0)
    {
        frame = null;

        lock (_gate)
        {
            long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);

            while (_ready.Count == 0)
            {
                if (_stopped) return false;
                if (_eof) return false;
                if (timeoutMs <= 0) return false;

                int remain = (int)(deadline - Environment.TickCount64);
                if (remain <= 0) return false;
                Monitor.Wait(_gate, remain);
            }

            frame = _ready.Dequeue();
            _inFlight++;
            return true;
        }
    }

    /// <summary>把帧还给缓冲池。</summary>
    public void Return(byte[]? frame)
    {
        if (frame == null) return;

        lock (_gate)
        {
            if (_inFlight > 0) _inFlight--;
            // 大小对不上说明还错了帧（或者是别的流的），丢了免得污染缓冲池
            if (frame.Length == FrameBytes)
            {
                _free.Enqueue(frame);
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void ReaderLoop()
    {
        try
        {
            while (true)
            {
                byte[] slot;

                lock (_gate)
                {
                    while (_free.Count == 0 && !_stopped) Monitor.Wait(_gate);
                    if (_stopped) return;
                    slot = _free.Dequeue();
                }

                bool ok = ReadExact(slot, FrameBytes);

                lock (_gate)
                {
                    if (_stopped) return;

                    if (!ok)
                    {
                        _eof = true;
                        Monitor.PulseAll(_gate);
                        return;
                    }

                    _ready.Enqueue(slot);
                    Monitor.PulseAll(_gate);
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_error.Length == 0) _error = $"{ex.GetType().Name}：{ex.Message}";
                _eof = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>读满一帧。读到一半就断了（帧尾）也算失败 —— 残缺的帧贴上去会花屏。</summary>
    private bool ReadExact(byte[] buffer, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n;
            try
            {
                n = _stdout!.Read(buffer, got, count - got);
            }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }

            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    /// <summary>
    /// 把 stderr 读掉，顺手留最后几行。
    ///
    /// 不读的话 ffmpeg 一旦往 stderr 写满管道（默认 4 KB）就会卡住不动 ——
    /// 表现为"动画莫名其妙停住"，而日志里什么都没有。
    /// </summary>
    private void DrainLoop()
    {
        try
        {
            using var err = _process.StandardError;
            string? line;
            while ((line = err.ReadLine()) != null)
            {
                lock (_gate)
                {
                    _stderrTail.AppendLine(line);
                    // 只留最后一小段：报错一般也就一行
                    if (_stderrTail.Length > 2048)
                        _stderrTail.Remove(0, _stderrTail.Length - 2048);
                }
            }
        }
        catch
        {
            // stderr 读不到不是问题，读帧那边才决定成败
        }
    }

    /// <summary>诊断报告里的一行。</summary>
    public string Describe()
    {
        var state = _stopped ? "已停止"
                  : IsEof ? "已解完"
                  : $"运行中（已备 {ReadyCount}/{BufferCount} 帧）";

        var text = $"{Path.GetFileName(Path.GetDirectoryName(VideoPath))}：" +
                   $"格子 {CellSide}×{CellSide}、每帧 {FrameBytes / 1024.0:0.#} KB、{state}";

        var err = Error;
        if (err.Length > 0) text += Environment.NewLine + "      " + err.Replace("\n", "\n      ");

        return text;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _ready.Clear();
            Monitor.PulseAll(_gate);
        }

        try
        {
            if (!_process.HasExited)
            {
                // 整棵进程树一起杀：ffmpeg 自己不会再拉子进程，但这么写更保险
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 已经退出了、或者没权限 —— 都不影响后续
        }

        try { _stdout?.Dispose(); } catch { /* 关闭路径上不再抛 */ }
        try { _process.Dispose(); } catch { /* 同上 */ }

        // 给线程一点时间收尾，但绝不为它们卡住界面
        try { _reader?.Join(300); } catch { }
        try { _drain?.Join(300); } catch { }
    }
}
