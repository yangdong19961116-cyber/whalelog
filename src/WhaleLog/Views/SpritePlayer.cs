using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
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
/// 帧表驱动的精灵图播放器：往一个 <see cref="Image"/> 上贴帧。
///
/// 两条设计约束值得说明：
///
/// 1. **时间由时钟算，不由"收到了几次事件"算**：每帧该显示第几帧是
///    「已过时间 ÷ 单帧时长」，事件来早了来晚了都不会让动画变快变慢。
///    渲染时钟大约 64Hz，24fps 的动画落在 2~3 个 tick 上，
///    靠计次会累积成明显的忽快忽慢。
///
/// 2. **渲染时钟 + 兜底计时器双通道**：CompositionTarget.Rendering 在窗口不可见
///    （被隐藏、会话锁定、远程桌面）时根本不触发。只挂它的话，动画会永远卡在
///    "正在播放"，后面所有动作都被堵住。多挂一个 DispatcherTimer 兜底，
///    两个通道都调同一个幂等的 Advance()，谁先到谁推进。
///
/// 素材是按需加载的：帧表在扫描阶段就解析好了（很便宜），
/// 真正的位图等第一次播到才解码 —— 没被用到的动作不占内存。
/// </summary>
public sealed class SpritePlayer : IDisposable
{
    private sealed class Entry
    {
        public required AnimSheet Sheet { get; init; }
        public required string ImagePath { get; init; }
        public SpriteAnimation? Loaded { get; set; }
        public string LoadError { get; set; } = "";
    }

    private readonly Image _target;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _fallback;
    private readonly Dispatcher _dispatcher;

    private SpriteAnimation? _current;
    private int _shownFrame = -1;
    private bool _hooked;

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

    public bool IsPlaying => _current != null;

    /// <summary>正在播的动作名，没在播时为 null。</summary>
    public string? CurrentAction => _current?.Action;

    /// <summary>当前显示的是第几帧（-1 = 没在播）。诊断用。</summary>
    public int CurrentFrame { get; private set; } = -1;

    /// <summary>登记过的动作数（含还没解码的）。</summary>
    public int RegisteredCount => _entries.Count;

    /// <summary>已经解好码、可以直接播的动作数。</summary>
    public int LoadedCount => _entries.Values.Count(e => e.Loaded != null);

    /// <summary>最近一次加载失败的原因，按动作名。诊断报告用。</summary>
    public IReadOnlyDictionary<string, string> LoadErrors =>
        _entries.Where(e => e.Value.LoadError.Length > 0)
                .ToDictionary(e => e.Key, e => e.Value.LoadError, StringComparer.Ordinal);

    public bool Has(string action) => _entries.ContainsKey(AnimActions.Canonical(action));

    /// <summary>登记一个动作。重复登记同一个动作键时后者覆盖（素材重扫的情形）。</summary>
    public void Register(string action, AnimSheet sheet, string imagePath)
    {
        var key = AnimActions.Canonical(action);
        _entries[key] = new Entry { Sheet = sheet, ImagePath = imagePath };
    }

    public void Clear()
    {
        Stop();
        _entries.Clear();
    }

    /// <summary>
    /// 播一个动作。<paramref name="restart"/> 为 false 时，正在播同一个动作就不打断
    /// （闲置眨眼很密集，不判断就会出现"眨到一半重新开始"的抽搐感）。
    /// </summary>
    public bool Play(string action, bool restart = true)
    {
        var key = AnimActions.Canonical(action);
        if (!_entries.TryGetValue(key, out var entry)) return false;

        if (_current != null)
        {
            if (!restart && _current.Action == key) return true;
            _current = null;                 // 打断：不再通知 Finished，避免调用方连环接力
            CurrentFrame = -1;
            _shownFrame = -1;
        }

        if (entry.Loaded == null)
        {
            entry.Loaded = SpriteAnimation.Load(key, entry.Sheet, entry.ImagePath, out var error);
            entry.LoadError = error;

            if (entry.Loaded == null)
            {
                CrashLog.Note($"[动画] {key} 加载失败，本次跳过：{error}");
                return false;
            }
        }

        _current = entry.Loaded;
        _shownFrame = -1;
        CurrentFrame = -1;
        _injected = TimeSpan.Zero;
        _clock.Restart();

        Hook();
        Advance();      // 立刻贴上第 0 帧：等下一个 tick 才显示会有"点下去没反应"的迟滞感

        return true;
    }

    /// <summary>立刻停下并回到静态图。</summary>
    public void Stop()
    {
        _current = null;
        _shownFrame = -1;
        CurrentFrame = -1;
        _clock.Reset();
        _injected = TimeSpan.Zero;

        Unhook();
        RestoreBase();

        // 主动 Stop 不抛 Finished —— 否则调用方会以为"这段播完了，该接着播下一段"
    }

    /// <summary>
    /// 推进到"当前时间该显示的那一帧"。幂等：两个时钟通道都会调它。
    /// </summary>
    private void Advance() => ApplyFrame(ElapsedMs);

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
        _target.Source = anim.Frames[index];
    }

    /// <summary>
    /// 已经播了多久 = 真实时钟 + 注入的时间。
    /// 探针需要的"确定性推进"就靠后半截，见 <see cref="AdvanceBy"/>。
    /// </summary>
    private double ElapsedMs => _clock.Elapsed.TotalMilliseconds + _injected.TotalMilliseconds;

    private void Finish()
    {
        var action = _current?.Action;
        _current = null;
        _shownFrame = -1;
        CurrentFrame = -1;
        _clock.Reset();
        _injected = TimeSpan.Zero;

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
        if (_current == null) return;
        _injected += delta;
        ApplyFrame(ElapsedMs);
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

        if (_fallback.IsEnabled) _fallback.Stop();
    }

    private void OnRendering(object? sender, EventArgs e) => Advance();

    public void Dispose()
    {
        Unhook();
        _entries.Clear();
        _current = null;
    }
}
