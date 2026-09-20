using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhaleLog.Services;

namespace WhaleLog.Views;

/// <summary>
/// 一段已经切好帧的动画。
///
/// 帧是 <see cref="CroppedBitmap"/>：不复制像素，只记录"看精灵图的哪一块"，
/// 所以 8 帧的内存开销是几十个字节，真正的位图只有精灵图那一张。
/// </summary>
public sealed class SpriteAnimation
{
    public string Action { get; }
    public AnimSheet Sheet { get; }
    public IReadOnlyList<CroppedBitmap> Frames { get; }

    public int FrameCount => Frames.Count;
    public TimeSpan FrameDuration => Sheet.FrameDuration;

    private SpriteAnimation(string action, AnimSheet sheet, IReadOnlyList<CroppedBitmap> frames)
    {
        Action = action;
        Sheet = sheet;
        Frames = frames;
    }

    /// <summary>
    /// 从磁盘加载一段动画。切帧失败返回 null 并给出**人能看懂**的原因 ——
    /// 这条路走不通时界面要退回静态形象，所以原因必须能被写进诊断日志。
    /// </summary>
    public static SpriteAnimation? Load(
        string action, AnimSheet sheet, string sheetImagePath, out string error)
    {
        error = "";

        try
        {
            if (!File.Exists(sheetImagePath))
            {
                error = $"找不到精灵图：{sheetImagePath}";
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(sheetImagePath);
            // OnLoad：立刻读完并释放文件句柄，否则用户的素材文件会被程序占住，
            // 想换图时得到「文件正在使用中」。
            image.CacheOption = BitmapCacheOption.OnLoad;
            // 无视 WPF 按 URI 的图片缓存，否则改了文件内容还显示旧图
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            image.Freeze();

            var (expectW, expectH) = sheet.ExpectedSheetSize;
            if (image.PixelWidth < expectW || image.PixelHeight < expectH)
            {
                error = $"精灵图只有 {image.PixelWidth}×{image.PixelHeight}，" +
                        $"按帧表需要 {expectW}×{expectH}（{sheet.FrameWidth}×{sheet.Cols} × {sheet.FrameHeight}×{sheet.Rows}）";
                return null;
            }

            var frames = new List<CroppedBitmap>(sheet.Count);
            for (int i = 0; i < sheet.Count; i++)
            {
                if (!sheet.TryFrameRect(i, out var x, out var y, out var w, out var h)) break;

                var crop = new CroppedBitmap(image, new System.Windows.Int32Rect(x, y, w, h));
                // 冻结之后 WPF 才能把它交给渲染线程复用；不冻结每帧都要重算
                crop.Freeze();
                frames.Add(crop);
            }

            if (frames.Count == 0)
            {
                error = "一帧都切不出来（帧表的 count / cols / rows 互相矛盾）";
                return null;
            }

            return new SpriteAnimation(action, sheet, frames);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}：{ex.Message}";
            return null;
        }
    }
}

/// <summary>
/// 帧表驱动的动画播放器：往一个 <see cref="Image"/> 上贴帧。
/// 两种素材都走它：
///
///   · **精灵图**（sheet.png + sheet.json）—— 帧是 CroppedBitmap，按时间轴挑第几帧。
///   · **透明视频**（video.webm + anim.json）—— 帧是 ffmpeg 现解出来的 RGBA，
///     由 <see cref="VideoFrameStream"/> 在后台喂过来。
///
/// 三条设计约束值得说明：
///
/// 1. **时间由时钟算，不由"收到了几次事件"算**：每帧该显示第几帧是
///    「已过时间 ÷ 单帧时长」，事件来早了来晚了都不会让动画变快变慢。
///    渲染时钟大约 64Hz，24fps 的动画落在 2~3 个 tick 上，
///    靠计次会累积成明显的忽快忽慢。视频那条路同理：到点了才去缓冲里取下一帧。
///
/// 2. **渲染时钟 + 兜底计时器双通道**：CompositionTarget.Rendering 在窗口不可见
///    （被隐藏、会话锁定、远程桌面）时根本不触发。只挂它的话，动画会永远卡在
///    "正在播放"，后面所有动作都被堵住。多挂一个 DispatcherTimer 兜底，
///    两个通道都调同一个幂等的 Advance()，谁先到谁推进。
///
/// 3. **一个 tick 最多贴一帧**：视频解码偶发跟不上时，积压的帧不能一口气全贴出去
///    （那样会看到一阵快进）。落后了就一帧一帧追，视觉上只是稍微慢半拍。
///
/// 素材是按需加载的：帧表在扫描阶段就解析好了（很便宜），
/// 真正的位图等第一次播到才解码 —— 没被用到的动作不占内存。
/// 视频更彻底：一帧都不预存，全靠 <see cref="VideoFrameStream"/> 边播边解。
/// </summary>
public sealed class SpritePlayer : IDisposable
{
    private sealed class Entry
    {
        public required string Action { get; init; }
        public required AnimSheet Sheet { get; init; }

        /// <summary>精灵图 PNG 或视频 webm 的完整路径（由帧表的 type 决定）。</summary>
        public required string MediaPath { get; init; }

        /// <summary>精灵图专用：切好的帧。</summary>
        public SpriteAnimation? Loaded { get; set; }

        /// <summary>视频专用：正在解码的这条流（也可能是预热好的、还没播的）。</summary>
        public VideoFrameStream? Stream { get; set; }

        public string LoadError { get; set; } = "";

        public bool IsVideo => Sheet.IsVideo;
    }

    /// <summary>视频正在播的那一段。</summary>
    private sealed class VideoPlay
    {
        public required Entry Entry { get; init; }
        public required VideoFrameStream Stream { get; init; }

        /// <summary>复用的位图。每帧新建一个 576 KB 的位图会把 GC 压垮，所以只建一次。</summary>
        public WriteableBitmap? Bitmap;

        /// <summary>已经贴出去多少帧。</summary>
        public int Shown;
    }

    private readonly Image _target;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _fallback;
    private readonly Dispatcher _dispatcher;

    private SpriteAnimation? _current;
    private VideoPlay? _video;
    private int _shownFrame = -1;
    private bool _hooked;

    /// <summary>预热好的那条流（悬停时提前起 ffmpeg，点下去就不用等启动）。</summary>
    private Entry? _warm;

    /// <summary>
    /// 探针注入的时间（见 <see cref="AdvanceBy"/>）。真实播放时恒为 0。
    /// 单独记成一个量而不是去改 Stopwatch —— Stopwatch 没法快进，
    /// 而"已经播了多久"必须是「真实时钟 + 注入量」，两个入口才能共用同一套判定。
    /// </summary>
    private TimeSpan _injected = TimeSpan.Zero;

    /// <summary>一段动画播完（或被打断）时触发，参数是动作名。</summary>
    public event Action<string>? Finished;

    public SpritePlayer(Image target)
    {
        _target = target;
        _dispatcher = target.Dispatcher;
        _fallback = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _fallback.Tick += (_, _) => Advance();
    }

    /// <summary>
    /// 静止时显示的图。播完一段动画就回到它。
    /// 设成 null 表示"别动 Source"（取不到替代图时宁可留着原来那张，也不要把鲸鱼弄没）。
    /// </summary>
    public BitmapSource? BaseImage { get; set; }

    public bool IsPlaying => _current != null || _video != null;

    /// <summary>正在播的动作名，没在播时为 null。</summary>
    public string? CurrentAction => _video?.Entry.Action ?? _current?.Action;

    /// <summary>正在播的是视频（而不是精灵图）。</summary>
    public bool CurrentIsVideo => _video != null;

    /// <summary>当前显示的是第几帧（-1 = 没在播）。诊断用。</summary>
    public int CurrentFrame { get; private set; } = -1;

    /// <summary>视频已经贴出去多少帧（精灵图时为 0）。自检用。</summary>
    public int VideoFramesShown => _video?.Shown ?? 0;

    /// <summary>本次播放里是否真的贴出过一帧。用来判断"贴的是动画帧还是底图"。</summary>
    public bool FrameShown { get; private set; }

    /// <summary>登记过的动作数（含还没解码的）。</summary>
    public int RegisteredCount => _entries.Count;

    /// <summary>已经解好码、可以直接播的动作数（视频算"已就绪"要等到真的解出帧）。</summary>
    public int LoadedCount => _entries.Values.Count(e => e.Loaded != null || e.Stream is { IsRunning: true });

    /// <summary>登记的动作里有几个是透明视频。</summary>
    public int VideoCount => _entries.Values.Count(e => e.IsVideo);

    /// <summary>用的 ffmpeg 在哪（诊断报告用）。</summary>
    public string FfmpegState => FfmpegLocator.Describe();

    /// <summary>最近一次加载失败的原因，按动作名。诊断报告用。</summary>
    public IReadOnlyDictionary<string, string> LoadErrors =>
        _entries.Where(e => e.Value.LoadError.Length > 0)
                .ToDictionary(e => e.Key, e => e.Value.LoadError, StringComparer.Ordinal);

    public bool Has(string action) => _entries.ContainsKey(AnimActions.Canonical(action));

    /// <summary>这个动作是不是视频（预热、诊断用）。</summary>
    public bool IsVideoAction(string action)
        => _entries.TryGetValue(AnimActions.Canonical(action), out var e) && e.IsVideo;

    /// <summary>登记一个动作。重复登记同一个动作键时后者覆盖（素材重扫的情形）。</summary>
    /// <param name="mediaPath">精灵图 PNG 或视频 webm 的完整路径。由 <paramref name="sheet"/> 的 type 决定怎么用。</param>
    public void Register(string action, AnimSheet sheet, string mediaPath)
    {
        var key = AnimActions.Canonical(action);

        // 覆盖登记之前先把旧的那个收干净（它还占着一个 ffmpeg 进程）
        if (_entries.TryGetValue(key, out var old)) ReleaseEntry(old);

        _entries[key] = new Entry { Action = key, Sheet = sheet, MediaPath = mediaPath };
    }

    public void Clear()
    {
        Stop();
        foreach (var entry in _entries.Values) ReleaseEntry(entry);
        _entries.Clear();
        _warm = null;
    }

    private void ReleaseEntry(Entry entry)
    {
        if (ReferenceEquals(_warm, entry)) _warm = null;
        if (_video != null && ReferenceEquals(_video.Entry, entry)) StopCurrent(disposeVideo: true);
        entry.Loaded = null;

        try { entry.Stream?.Dispose(); } catch { /* 关不掉不影响别的 */ }
        entry.Stream = null;
    }

    // ==================================================================
    // 播放
    // ==================================================================

    /// <summary>
    /// 播一个动作。<paramref name="restart"/> 为 false 时，正在播同一个动作就不打断
    /// （闲置眨眼很密集，不判断就会出现"眨到一半重新开始"的抽搐感）。
    /// </summary>
    public bool Play(string action, bool restart = true)
    {
        var key = AnimActions.Canonical(action);
        if (!_entries.TryGetValue(key, out var entry)) return false;

        if (IsPlaying)
        {
            if (!restart && CurrentAction == key) return true;

            // 打断：不再通知 Finished，避免调用方连环接力
            StopCurrent(disposeVideo: true);
            Unhook();
        }

        return entry.IsVideo ? StartVideo(entry) : StartSprite(entry);
    }

    private bool StartSprite(Entry entry)
    {
        if (entry.Loaded == null)
        {
            entry.Loaded = SpriteAnimation.Load(entry.Action, entry.Sheet, entry.MediaPath, out var error);
            entry.LoadError = error;

            if (entry.Loaded == null)
            {
                CrashLog.Note($"[动画] {entry.Action} 加载失败，本次跳过：{error}");
                return false;
            }
        }

        _current = entry.Loaded;
        _shownFrame = -1;
        CurrentFrame = -1;
        FrameShown = false;
        _injected = TimeSpan.Zero;
        _clock.Restart();

        Hook();
        Advance();      // 立刻贴上第 0 帧：等下一个 tick 才显示会有"点下去没反应"的迟滞感

        return true;
    }

    /// <summary>
    /// 起播一个视频动作。
    ///
    /// 优先用**预热好的**那条流：悬停时（鼠标移进悬浮窗）就已经把 ffmpeg 起好、
    /// 头几帧解在手上了，点下去不用等进程启动的那一百多毫秒。
    /// </summary>
    private bool StartVideo(Entry entry)
    {
        if (!FfmpegLocator.IsAvailable)
        {
            entry.LoadError = "没有 ffmpeg.exe，透明视频动作播不了。" + FfmpegLocator.Where;
            CrashLog.Note($"[动画] {entry.Action} 播不了：{entry.LoadError}");
            return false;
        }

        VideoFrameStream stream;

        if (entry.Stream is { IsRunning: true, IsEof: false })
        {
            stream = entry.Stream;
        }
        else
        {
            try { entry.Stream?.Dispose(); } catch { /* 旧的坏了就算了 */ }
            entry.Stream = null;

            try
            {
                stream = new VideoFrameStream(
                    FfmpegLocator.Path!,
                    entry.MediaPath,
                    entry.Sheet.Filter,
                    entry.Sheet.FrameWidth,
                    entry.Sheet.Loop);
            }
            catch (Exception ex)
            {
                entry.LoadError = $"起 ffmpeg 失败：{ex.Message}";
                CrashLog.Note($"[动画] {entry.Action} 播不了：{entry.LoadError}");
                return false;
            }

            entry.Stream = stream;
        }

        if (ReferenceEquals(_warm, entry)) _warm = null;

        entry.LoadError = "";
        _video = new VideoPlay { Entry = entry, Stream = stream };
        _shownFrame = -1;
        CurrentFrame = -1;
        FrameShown = false;
        _injected = TimeSpan.Zero;
        _clock.Restart();

        Hook();
        Advance();      // 第一帧可能已经在预热缓冲里了，立刻就贴

        return true;
    }

    /// <summary>
    /// 预热：提前把这套动作起好 ffmpeg 并把头几帧解到手。
    ///
    /// 只为"点下去要立刻有反应"这一个场景服务 —— 没预热也能播，只是要等
    /// ffmpeg 启动（实测冷启动约 150 ms，肉眼能感觉到"卡了一下"）。
    /// 同时只留一条预热流（多留几条就是多个常驻进程），
    /// 换目标时把上一条收掉。
    /// </summary>
    public void Warm(string action)
    {
        var key = AnimActions.Canonical(action);
        if (!_entries.TryGetValue(key, out var entry) || !entry.IsVideo) return;
        if (ReferenceEquals(_warm, entry)) return;

        ReleaseWarm();

        if (entry.Stream != null) { _warm = entry; return; }
        if (!FfmpegLocator.IsAvailable) return;

        try
        {
            entry.Stream = new VideoFrameStream(
                FfmpegLocator.Path!,
                entry.MediaPath,
                entry.Sheet.Filter,
                entry.Sheet.FrameWidth,
                entry.Sheet.Loop);

            _warm = entry;
        }
        catch (Exception ex)
        {
            entry.LoadError = $"预热失败：{ex.Message}";
        }
    }

    /// <summary>把没被用上的预热流收掉（鼠标移开悬浮窗时叫一声，别让 ffmpeg 常驻）。</summary>
    public void ReleaseWarm()
    {
        var entry = _warm;
        _warm = null;
        if (entry == null) return;

        // 正在播的那条不能动
        if (ReferenceEquals(_video?.Entry, entry)) return;
        if (ReferenceEquals(_current?.Sheet, entry.Sheet)) return;

        try { entry.Stream?.Dispose(); } catch { /* 关不掉不影响别的 */ }
        entry.Stream = null;
    }

    /// <summary>立刻停下并回到静态图。</summary>
    public void Stop()
    {
        StopCurrent(disposeVideo: true);
        Unhook();
        RestoreBase();

        // 主动 Stop 不抛 Finished —— 否则调用方会以为"这段播完了，该接着播下一段"
    }

    /// <summary>停掉当前这一段（精灵图或视频），不通知 Finished。</summary>
    private void StopCurrent(bool disposeVideo)
    {
        _current = null;

        var video = _video;
        _video = null;

        if (video != null && disposeVideo)
        {
            video.Entry.Stream = null;
            try { video.Stream.Dispose(); } catch { /* 关不掉不影响别的 */ }
        }

        _shownFrame = -1;
        CurrentFrame = -1;
        FrameShown = false;
        _clock.Reset();
        _injected = TimeSpan.Zero;
    }

    /// <summary>
    /// 推进到"当前时间该显示的那一帧"。幂等：两个时钟通道都会调它。
    /// </summary>
    private void Advance()
    {
        if (_video != null) AdvanceVideo();
        else ApplyFrame(ElapsedMs);
    }

    private void ApplyFrame(double elapsedMs)
    {
        var anim = _current;
        if (anim == null) return;

        double frameMs = anim.FrameDuration.TotalMilliseconds;
        if (frameMs <= 0) { Finish(); return; }

        int index = (int)(elapsedMs / frameMs);

        if (index >= anim.FrameCount)
        {
            if (!anim.Sheet.Loop) { Finish(); return; }
            index %= anim.FrameCount;
        }

        if (index == _shownFrame) return;    // 还没到下一帧（渲染时钟比帧率快时是常态）

        _shownFrame = index;
        CurrentFrame = index;
        FrameShown = true;
        _target.Source = anim.Frames[index];
    }

    /// <summary>
    /// 视频的推进：**到点了才去缓冲里取一帧**，一个 tick 最多贴一帧。
    ///
    /// 解还没跟上时不推进时间轴（本次直接返回，下个 tick 再试）——
    /// 表现出来的只是"稍慢半拍"，而不是跳帧或快进。
    /// </summary>
    private void AdvanceVideo()
    {
        var video = _video;
        if (video == null) return;

        double frameMs = 1000.0 / Math.Max(1.0, video.Entry.Sheet.Fps);

        // 还没到下一帧该出场的时间（渲染时钟 64Hz、帧率 24fps，多数 tick 都会停在这儿）
        if (ElapsedMs < (video.Shown + 1) * frameMs) return;

        if (!video.Stream.TryTake(out var pixels))
        {
            // 解完了、手上也没有待取的帧 → 这一段落幕
            if (video.Stream.IsDrained) Finish();
            return;
        }

        PresentVideoFrame(video, pixels!);
        video.Stream.Return(pixels);

        video.Shown++;
        CurrentFrame = video.Shown - 1;
        FrameShown = true;
    }

    private void PresentVideoFrame(VideoPlay video, byte[] pixels)
    {
        int side = video.Entry.Sheet.FrameWidth;

        // 复用同一张位图：每帧新建一张 576 KB 的会把 GC 压垮（24fps = 每秒 13 MB 垃圾）
        // ⚠️ Bgra32 的内存序是 B,G,R,A，所以解码器必须吐 bgra
        //    （VideoFrameStream.PixelFormat）。这两处是一对，改一边就必须改另一边 ——
        //    对不上时的表现是红蓝互换、不报任何错。
        video.Bitmap ??= new WriteableBitmap(side, side, 96, 96, PixelFormats.Bgra32, null);
        video.Bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, side, side), pixels, side * 4, 0);

        if (!ReferenceEquals(_target.Source, video.Bitmap)) _target.Source = video.Bitmap;
    }

    /// <summary>
    /// 已经播了多久 = 真实时钟 + 注入的时间。
    /// 探针需要的"确定性推进"就靠后半截，见 <see cref="AdvanceBy"/>。
    /// </summary>
    private double ElapsedMs => _clock.Elapsed.TotalMilliseconds + _injected.TotalMilliseconds;

    private void Finish()
    {
        var action = CurrentAction;

        StopCurrent(disposeVideo: true);
        Unhook();
        RestoreBase();

        if (action != null) Finished?.Invoke(action);
    }

    /// <summary>
    /// 诊断自检用：不真的等下一个渲染 tick，直接把时间轴往前推。
    ///
    /// 真实的播放节奏取决于显示器刷新率（约 64Hz）和系统调度，
    /// 靠"睡一会儿再看第几帧"写断言会时灵时不灵。走这个入口就能问一句
    /// 「时间过了两帧，现在该显示第几帧」—— 确定性的。
    /// </summary>
    public void AdvanceBy(TimeSpan delta)
    {
        if (!IsPlaying) return;
        _injected += delta;
        Advance();
    }

    private void RestoreBase()
    {
        // BaseImage 为 null 时不动 Source：宁可停在最后一帧，也不要把鲸鱼变没
        if (BaseImage != null) _target.Source = BaseImage;
    }

    // ------------------------------------------------------------------
    // 时钟订阅
    // ------------------------------------------------------------------

    private void Hook()
    {
        if (_hooked) return;

        try
        {
            System.Windows.Media.CompositionTarget.Rendering += OnRendering;
            _hooked = true;
        }
        catch
        {
            // 渲染时钟挂不上（极端环境）—— 还有兜底计时器
        }

        if (!_fallback.IsEnabled) _fallback.Start();
    }

    private void Unhook()
    {
        if (_hooked)
        {
            try { System.Windows.Media.CompositionTarget.Rendering -= OnRendering; }
            catch { /* 关闭路径上不再抛 */ }
            _hooked = false;
        }

        if (!_fallback.IsEnabled) return;
        _fallback.Stop();
    }

    private void OnRendering(object? sender, EventArgs e) => Advance();

    public void Dispose()
    {
        StopCurrent(disposeVideo: true);
        _warm = null;
        Unhook();

        foreach (var entry in _entries.Values)
        {
            try { entry.Stream?.Dispose(); } catch { /* 关闭路径上不再抛 */ }
            entry.Stream = null;
            entry.Loaded = null;
        }

        _entries.Clear();
    }
}
