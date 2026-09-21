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
/// ⚠️ VP9 的 alpha 必须点名用 libvpx 解码器
/// --------------------------------------
/// 这是踩过的坑：透明 webm 的 alpha 不是存在像素里，而是 VP9 的**附加数据**
/// （matroska 的 BlockAdditional，文件头里能看到 ALPHA_MODE=1）。
///
/// ffmpeg 默认挑的 native vp9 解码器**不认这层附加数据**，它照样给你一帧帧像素，
/// 但 alpha 位全填 255 —— 于是"能播、帧数也对、一个错都不报"，
/// 只是角色变成一个**带黑底的方形贴片**：透明区成了黑块，边缘的羽化没了变成硬边，
/// 看上去就像"锯齿很严重"。这种错不会抛异常，光看帧数根本发现不了，
/// 所以 1.4.1 之前一直都在悄悄发生。
///
/// 点名 `-c:v libvpx-vp9` 之后像素格式会从 yuv420p 变成 yuva420p，alpha 才回到手上。
/// 实测代价很小：同一个动作（241 帧）native 0.155 秒、libvpx 0.183 秒，只慢 18%，
/// 远在"解码比实时快 60 倍"的安全范围里。
///
/// 探测分两步（见 <see cref="IsVp9Webm"/>、<see cref="FfmpegSupportsVp9Alpha"/>）：
/// 先看素材是不是 VP9（webm 的 CodecID 在文件头是明文），再问 ffmpeg 有没有这个解码器。
/// 两条都满足才加点名参数 —— 否则遇到 mpeg4 之类的素材会因为"解码器不匹配"直接解不了。
/// 万一本机 ffmpeg 是个精简版、没有 libvpx-vp9，就退回 native：至少还能播，
/// 只是没有透明（真遇到时 <see cref="HasAlpha"/> 会是 false，诊断报告里看得见）。
///
/// ⚠️ 像素字节序必须是 bgra（红蓝别弄反）
/// ------------------------------------
/// 这是第二个同类型的坑：解出来的帧给的是**裸字节**，谁都不报错，只是颜色是错的。
///
/// ffmpeg 的 `-pix_fmt rgba` 吐的是 R,G,B,A 顺序的字节，而 WPF 的
/// <c>PixelFormats.Bgra32</c>（<see cref="SpritePlayer"/> 贴图用的就是它）内存里是
/// **B,G,R,A** —— 把 rgba 的字节直接丢进去，等于把红蓝两条通道对调。
///
/// 症状很唬人：深蓝紫的头发会变成栗棕红，看着"像反色了"，而**所有帧都一样错**、
/// 不花屏、不抖动、像素数一个不差，所以任何"帧数对不对""字节数对不对"的断言都拦不住。
/// 1.4.0 / 1.4.1 / 1.4.2 三个包都是这个颜色。
///
/// 正确做法是让 ffmpeg 直接吐 <c>bgra</c>（swscale 内部顺手就换了，不额外花时间），
/// 这样字节序和 WPF 天然对齐，C# 侧一个像素都不用动。
/// 自检里的「透明通道」那节会在纯红素材上断言红值确实落在 R 通道，防止再被改回去。
///
/// ⚠️ 画布不再是"方的"（2026-09-21）
/// --------------------------------
/// 早期只有原版那套 610×610 素材，于是帧缓冲、行距、位图全按**单边**算。
/// dsh 的视频原画布是 640×360（16:9），为了塞进方形格子只能 `crop` 成 384×384 ——
/// 逐帧量过：106 个动作里 25 个有内容被切（蝴蝶/烟花/气球这类带道具的最狠，
/// 左右各切掉 120px）。用户看到的就是"新形象显示被裁掉"。
///
/// 现在宽高分开算（<see cref="FrameWidth"/> / <see cref="FrameHeight"/>），
/// 源多大画布就多大，不再有"为了变方而裁"这一步。
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

    /// <summary>
    /// 解码输出的像素格式（就是 ffmpeg 的 -pix_fmt 值）。
    ///
    /// **必须是 bgra**，因为 <see cref="SpritePlayer"/> 把它直接写进
    /// <c>PixelFormats.Bgra32</c>（内存序 B,G,R,A）。改成 rgba 会让红蓝互换 ——
    /// 不报错、不花屏，只是颜色全错，见类头注释。
    /// 由此字节偏移固定是：0=B、1=G、2=R、3=A。
    /// </summary>
    public const string PixelFormat = "bgra";

    /// <summary>字节偏移：蓝。</summary>
    public const int OffsetB = 0;
    /// <summary>字节偏移：绿。</summary>
    public const int OffsetG = 1;
    /// <summary>字节偏移：红。</summary>
    public const int OffsetR = 2;
    /// <summary>字节偏移：透明度。</summary>
    public const int OffsetA = 3;

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

    /// <summary>画布宽（解码后一帧的像素宽）。</summary>
    public int FrameWidth { get; }

    /// <summary>画布高（解码后一帧的像素高）。</summary>
    public int FrameHeight { get; }

    /// <summary>
    /// 画布是方的（宽=高）。
    ///
    /// 留着它是因为"方的"曾经是硬假设：早期只有原版那一套 610×610 的素材，
    /// 帧缓冲、行距、位图全按单边算。dsh 那套视频的原画布是 16:9，
    /// 当年为了塞进方形格子只能 crop 成方的 —— 代价是道具被裁（见类头）。
    /// 现在两轴分开算了，这个属性只用于诊断报告里说一句"这是方形画布"。
    /// </summary>
    public bool IsSquare => FrameWidth == FrameHeight;

    /// <summary>素材是 VP9（webm）—— 也就是"透明藏在附加数据里、必须点名 libvpx"的那一类。</summary>
    public bool IsVp9Source { get; }

    /// <summary>真的点名了 libvpx-vp9 解码器。</summary>
    public bool UsingLibvpx { get; }

    /// <summary>
    /// 这段素材解出来的帧带不带真 alpha。
    ///
    /// VP9 素材却没用上 libvpx 时是 <c>false</c>：帧照样能解、帧数也对，
    /// 但 alpha 全是 255，角色会顶着黑底显示。诊断报告要专门把这种情况点出来 ——
    /// 它是"看起来完全正常、实际画面全错"的那类故障。
    /// （非 VP9 素材本来就设计成不透明，这里算 true，不算问题。）
    /// </summary>
    public bool HasAlpha => !IsVp9Source || UsingLibvpx;

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
    /// <param name="filter">把源画布裁进画布的滤镜串（来自帧表，已校验过）。留空表示原样用源分辨率。</param>
    /// <param name="frameWidth">画布宽，必须和滤镜的输出尺寸一致。</param>
    /// <param name="frameHeight">画布高，必须和滤镜的输出尺寸一致。</param>
    /// <param name="loop">true 时用 -stream_loop -1 让输入无缝循环（待机、拖拽这类要一直播的）。</param>
    /// <param name="bufferCount">环形缓冲槽数。</param>
    public VideoFrameStream(
        string ffmpegExe, string videoPath, string filter,
        int frameWidth, int frameHeight, bool loop,
        int bufferCount = DefaultBufferCount)
    {
        if (string.IsNullOrWhiteSpace(ffmpegExe)) throw new ArgumentException("ffmpeg 路径为空", nameof(ffmpegExe));
        if (string.IsNullOrWhiteSpace(videoPath)) throw new ArgumentException("视频路径为空", nameof(videoPath));

        // 两轴各自校验。以前只校验单边（因为只支持方形），
        // 现在 16:9 的画布也走得通，宽高就得分别看。
        if (frameWidth < MinCellSide || frameWidth > MaxCellSide)
            throw new ArgumentOutOfRangeException(nameof(frameWidth),
                $"画布宽 {frameWidth} 超出 {MinCellSide}–{MaxCellSide}");
        if (frameHeight < MinCellSide || frameHeight > MaxCellSide)
            throw new ArgumentOutOfRangeException(nameof(frameHeight),
                $"画布高 {frameHeight} 超出 {MinCellSide}–{MaxCellSide}");

        if (!File.Exists(ffmpegExe)) throw new FileNotFoundException("找不到 ffmpeg", ffmpegExe);
        if (!File.Exists(videoPath)) throw new FileNotFoundException("找不到视频素材", videoPath);

        BufferCount = Math.Clamp(bufferCount, 2, 32);
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        FrameBytes = frameWidth * frameHeight * 4;
        VideoPath = videoPath;

        // 透明 webm 的 alpha 得点名要 libvpx 才给（原因见类头注释）
        IsVp9Source = IsVp9Webm(videoPath);
        UsingLibvpx = IsVp9Source && FfmpegSupportsVp9Alpha(ffmpegExe);

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
        // rawvideo + bgra：直接给像素，不带任何容器头 —— 每帧定长，按字节数切就行。
        //   bgra 而不是 rgba：WPF 的 PixelFormats.Bgra32 内存序就是 B,G,R,A，
        //   吐 rgba 的话红蓝会互换（深蓝头发变栗棕，见类头注释）。
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        if (loop)
        {
            psi.ArgumentList.Add("-stream_loop");
            psi.ArgumentList.Add("-1");
        }
        if (UsingLibvpx)
        {
            // 输入侧选项，必须在 -i 之前。不加这一句 alpha 会被 native 解码器丢掉。
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("libvpx-vp9");
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
        psi.ArgumentList.Add(PixelFormat);
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

        var alpha = !IsVp9Source ? "不透明素材"
                  : UsingLibvpx ? "透明正常"
                  : "⚠ 透明丢了（这台 ffmpeg 没有 libvpx-vp9 解码器）";

        var text = $"{Path.GetFileName(Path.GetDirectoryName(VideoPath))}：" +
                   $"画布 {FrameWidth}×{FrameHeight}、每帧 {FrameBytes / 1024.0:0.#} KB、{alpha}、{state}";

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

    // ------------------------------------------------------------------
    // 素材与解码器的探测（都是静态的，跟某一个流无关）
    // ------------------------------------------------------------------

    /// <summary>VP9 的 CodecID。webm 里它是明文，能直接在文件头里搜到。</summary>
    private const string Vp9CodecId = "V_VP9";

    /// <summary>问过的 ffmpeg 记在这儿，免得每开一个动作都查一遍。</summary>
    private static readonly Dictionary<string, bool> _libvpxCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 这个文件是不是 VP9 编码的 webm。
    ///
    /// 刻意不起 ffmpeg 探测：webm 是 EBML 容器，CodecID 是**明文**写在文件开头的
    /// （就是 ASCII 的 "V_VP9"）。读头 64 KB 搜一下就够了，比起一个进程快几个数量级 ——
    /// 而这是每次开始播动作都要走的一次判断。
    /// </summary>
    public static bool IsVp9Webm(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[64 * 1024];
            int n = fs.Read(buf, 0, buf.Length);
            if (n <= 0) return false;

            var needle = Encoding.ASCII.GetBytes(Vp9CodecId);
            var span = buf.AsSpan(0, n);

            for (int i = 0; i + needle.Length <= span.Length; i++)
            {
                if (span.Slice(i, needle.Length).SequenceEqual(needle)) return true;
            }
            return false;
        }
        catch
        {
            // 读不了就当它不是 —— 探测失败不该拦住播放
            return false;
        }
    }

    /// <summary>
    /// 这个 ffmpeg 认不认 libvpx-vp9 解码器。
    ///
    /// 便携包自带的那个有（实测 1.14.1）；但 PATH 里的精简版 ffmpeg 可能没有。
    /// 问一次记一次，不在热路径上反复起进程。
    /// </summary>
    public static bool FfmpegSupportsVp9Alpha(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;

        lock (_libvpxCache)
        {
            if (_libvpxCache.TryGetValue(exe, out var cached)) return cached;
            var ok = ProbeLibvpx(exe);
            _libvpxCache[exe] = ok;
            return ok;
        }
    }

    private static bool ProbeLibvpx(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-decoders");

            using var p = Process.Start(psi);
            if (p == null) return false;

            // 两边都异步读：`-decoders` 有一千多行，同步只读其中一边时
            // 另一边管道写满就会卡死在这儿
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.WaitForExit(5000);

            var text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            return text.Contains("libvpx-vp9", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>忘掉探测缓存。「重新加载素材」会用它。</summary>
    public static void ResetDecoderProbe()
    {
        lock (_libvpxCache) _libvpxCache.Clear();
    }
}
