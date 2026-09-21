using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhaleLog.Models;
using WhaleLog.Services;

namespace WhaleLog.Views;

/// <summary>
/// 桌面常驻的鲸鱼娘悬浮窗。
///
/// 三个状态：展开（鲸鱼 + 状态条）、收起（贴边小圆胶囊）、提醒气泡（叠加在展开态上方）。
/// 尺寸始终按「锚定边固定」的方式变化 —— 贴右边收起时右边缘不动、贴顶展开时上边缘不动，
/// 否则每次收起/展开都会看到窗口"跳"一下。
///
/// 位置与缩放写回 settings，重启后回到原地。
/// </summary>
public partial class FloatingWindow : Window
{
    private enum Edge { None, Left, Right, Top, Bottom }

    /// <summary>
    /// 展开态的**基准**窗口尺寸（设计像素）。真正的尺寸由
    /// <see cref="CharacterGeometry.LayoutFor"/> 按当前形象算 —— 原版正好落在这两个数上，
    /// 别的形象（比如 dsh 那套 16:9 画布）该宽就宽、该高就高。
    /// XAML 里 Width/Height 也写的这两个数，只用于设计器预览。
    /// </summary>
    private const double ExpandedWidth = CharacterGeometry.ExpandedWidth;
    private const double ExpandedHeight = CharacterGeometry.ExpandedHeight;
    private const double CollapsedSize = 60;
    private const double SnapThreshold = 40;

    /// <summary>
    /// 形象显示框的允许区间。
    ///
    /// 正常情况下它由形象包的几何算出来（原版 167.75、dsh 391×220、精灵图样例 198），
    /// 这里只是兜住手写坏的几何/帧表：太小看不清，太大能把窗口撑成一块占半个屏幕的空白。
    /// </summary>
    private const double MinWhaleBox = 120;
    private const double MaxWhaleBox = 512;

    private readonly DispatcherTimer _collapseTimer;

    /// <summary>
    /// 内容整体的缩放变换。挂在 RootGrid 的 LayoutTransform 上，
    /// 所以它会同时影响布局尺寸和渲染，鲸鱼、文字、间距一起等比变化。
    /// </summary>
    private readonly ScaleTransform _contentScale = new(1, 1);

    private bool _collapsed;
    private bool _dragging;
    private Edge _edge = Edge.None;
    private bool _ready;

    /// <summary>位置已经摆过一次了（那之后 ApplySize 才不会摸到 NaN）。</summary>
    private bool _positioned;

    // ---- 动态素材 ----
    /// <summary>扫描结果（诊断自检要读它写报告）。</summary>
    private AnimLibrary? _anim;

    /// <summary>精灵图播放器。没有可用素材时为 null，此时完全是静态形象。</summary>
    private SpritePlayer? _player;

    /// <summary>闲置眨眼的调度器。</summary>
    private DispatcherTimer? _blinkTimer;

    /// <summary>闲置小动作（ambient）的调度器 —— dsh 那套素材靠它随机冒一个动作出来。</summary>
    private DispatcherTimer? _ambientTimer;

    /// <summary>拖拽动作的延迟器：真的拖起来了才播（见 <see cref="ScheduleDragAnimation"/>）。</summary>
    private DispatcherTimer? _dragTimer;

    // ---- 形象包 ----
    /// <summary>当前生效的形象 ID（见 <see cref="CharacterPacks"/>）。</summary>
    private string _characterId = CharacterPacks.ClassicId;

    /// <summary>当前形象的素材根目录（形象包自己的那一层）。</summary>
    private string _characterRoot = "";

    /// <summary>上次按哪个形象建的播放器，设置改动后要重扫。</summary>
    private string _characterSettingApplied = "";
    private bool _characterSettingKnown;

    // ---- 按角色分好的动作 ----
    // 一个形象可以有一百多个动作，界面得知道"点击该播哪些""闲下来该冒什么"。
    // 这件事全在扫描时算一次，播放时只管挑（见 AnimActions.RoleOf）。
    private IReadOnlyList<string> _idleActions = Array.Empty<string>();
    private IReadOnlyList<string> _clickActions = Array.Empty<string>();
    private IReadOnlyList<string> _ambientActions = Array.Empty<string>();
    private IReadOnlyList<string> _dragActions = Array.Empty<string>();

    /// <summary>
    /// 归档表演的段落，**按顺序**（角色 celebrate，见 <see cref="AnimActions.Celebrate"/>）。
    ///
    /// 和上面几个角色不同，这一套不是"随机挑一个"而是"逐段演"：
    /// 归档成功后先低头清点，再高兴地跳一下 —— 顺序反了就不成样子。
    /// 素材包没有这个角色时（原版那套）这里是空的，归档就只是归档。
    /// </summary>
    private IReadOnlyList<string> _celebrateActions = Array.Empty<string>();

    /// <summary>还要接着演的段落（当前这段播完就轮到它们）。空 = 没有正在进行的表演。</summary>
    private readonly List<string> _celebrateQueue = new();

    /// <summary>
    /// 正在演的那一段。
    ///
    /// 单独记一份是为了判断"刚播完的到底是不是表演里的这一句"——
    /// 用户中途点了一下，播放器是**静默打断**的（打断不抛 Finished，见 SpritePlayer.Play），
    /// 光看队列会出现"等用户那次点击的动画播完后，突然接上庆祝的后半截"。
    /// </summary>
    private string? _celebrateCurrent;

    /// <summary>当前循环播着的待机动作（素材里可能有好几个，随机挑一个一直播）。</summary>
    private string? _loopingIdle;

    private static readonly Random BlinkRng = new();

    /// <summary>
    /// 当前形象包的画布几何。显示框、窗口尺寸都由它算 —— 见 <see cref="CharacterGeometry"/>。
    /// **每个形象包各有一份**，这是"两套模板不要统一高度"的落点。
    /// </summary>
    private CharacterGeometry _geometry = CharacterGeometry.Default;

    /// <summary>
    /// 当前形象的显示框（设计像素），宽高各算各的。
    ///
    /// 动态素材的画布比静态的大，这个框要跟着变大，否则角色会凭空缩小；
    /// 而宽和高必须分开算 —— 16:9 的画布塞进方形框就得先裁，道具会被切掉。
    /// 见 <see cref="CharacterGeometry.DisplayBox"/>。
    /// </summary>
    private (double W, double H) _whaleBox =
        (CharacterGeometry.ClassicBoxSide, CharacterGeometry.ClassicBoxSide);

    /// <summary>此刻是否启用了动态素材。</summary>
    private bool _animationsOn;

    /// <summary>上次按哪个"动画开关"值建的播放器，用来判断设置改动后要不要重建。</summary>
    private bool _animationsSettingApplied;
    private bool _animationsSettingKnown;

    // ---- 悬浮态形象（表情差分）----

    /// <summary>鼠标悬浮时替换成的形象。没配这张图时为 null，悬浮就什么都不发生。</summary>
    private BitmapSource? _hoverImage;

    /// <summary>静态本体。没在播动画、鼠标也不在上面时，显示的就是它。</summary>
    private BitmapSource? _stillImage;

    /// <summary>
    /// 鼠标此刻是不是在悬浮窗上。
    ///
    /// 显式记一个字段，而不是到处读 IsMouseOver —— 在 MouseLeave 处理函数里
    /// IsMouseOver 的取值时机依赖 WPF 内部顺序，拿不准；事件里自己置位就没这个问题。
    /// </summary>
    private bool _hovering;

    /// <summary>
    /// 此刻"静止"该显示哪一张：鼠标在上面且配了悬浮形象就用它，否则用本体。
    ///
    /// 不只是给 MouseEnter/Leave 用的 —— 动画播完要落回的那张"底图"（
    /// <see cref="SpritePlayer.BaseImage"/>）也必须是它。否则会出现
    /// 「鼠标悬停期间恰好播完一次眨眼，落回睁眼图」这种跳变。
    /// </summary>
    private BitmapSource? RestingImage
        => _hovering && _hoverImage != null ? _hoverImage : _stillImage;

    /// <summary>
    /// 展开态的设计尺寸。
    ///
    /// 显示框大一点，窗口就大一点 —— 两者必须出自同一个量。只把图放大而不动窗口，
    /// 多出来的部分会顶掉气泡的位置；只把窗口放大而不动图，鲸鱼下方会多一条空白。
    ///
    /// 原版（静态 167.75）算出来正好是 236×296，和改动前一个像素不差；
    /// dsh（框 391.1×220）算出来是 391.1×348.25 —— 画布宽，窗口就宽。
    /// </summary>
    private (double W, double H) ExpandedDesignSize
        => CharacterGeometry.LayoutFor(_whaleBox.W, _whaleBox.H);

    /// <summary>展开态的设计高度。</summary>
    private double ExpandedDesignHeight => ExpandedDesignSize.H;

    /// <summary>展开态的设计宽度。</summary>
    private double ExpandedDesignWidth => ExpandedDesignSize.W;

    // 按下时的屏幕坐标与窗口位置，用来判断这次到底是"拖拽"还是"点击"。
    // 都是屏幕坐标 / 绝对位置，不含任何随窗口移动而变的量。
    private System.Drawing.Point _dragStartCursor;
    private double _widgetLeftAtPress;
    private double _widgetTopAtPress;

    public FloatingWindow()
    {
        InitializeComponent();

        // 内容整体跟随窗口缩放。少了这一步，"大小"设置只会改变窗口宽高，
        // 里面的鲸鱼和文字纹丝不动 —— 放大时一片空，缩小时被裁。
        RootGrid.LayoutTransform = _contentScale;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!IsMouseOver && !_dragging && !_hasBubble) Collapse();
        };

        // 文件拖放：把文件丢到小鲸鱼上就归档。走 Preview 阶段，和主面板一致。
        AllowDrop = true;
        PreviewDragEnter += OnFileDragEnter;
        PreviewDragOver += OnFileDragOver;
        PreviewDragLeave += OnFileDragLeave;
        PreviewDrop += OnFileDrop;

        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        MouseLeftButtonDown += OnLeftButtonDown;
        MouseLeftButtonUp += OnLeftButtonUp;
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ((App)Application.Current).ShowTrayMenuAtCursor();
        };

        SourceInitialized += (_, _) => _ready = true;
        Loaded += (_, _) =>
        {
            // 动态素材必须在 RestorePosition 之前定下来 —— 它决定窗口该多高，
            // 反过来再摆位置就白摆一次。
            ReloadAnimations();

            RestorePosition();
            HookDataChanges();
            RefreshStatus();
        };

        // 显示/隐藏的时候待机循环要跟着停走：窗口不可见时动画没人看，
        // 但 ffmpeg 和渲染时钟还在跑 —— 白烧 CPU。
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) StartIdleLoop();
            else _player?.Stop();
        };
    }

    private bool _hasBubble;

    /// <summary>
    /// 诊断自检用：内容在窗口坐标里的垂直范围。
    /// 内容底边超出窗口高度就说明被裁掉了 —— "缩到最小会出现裁切"这类问题
    /// 只能靠量数字确认，眼睛看上去只是"有点挤"。
    /// </summary>
    public (double WhaleTop, double ContentBottom, double WindowHeight) MeasureLayout()
    {
        var whaleTop = WhaleImage.TranslatePoint(new Point(0, 0), this).Y;
        var bottom = StatusBar.TranslatePoint(new Point(0, StatusBar.ActualHeight), this).Y;
        return (whaleTop, bottom, ActualHeight);
    }

    /// <summary>诊断自检用：写入内存里的设置后重新套用（不落盘，避免污染用户配置）。</summary>
    public void ApplyScaleAndOpacity(double scale, double opacity)
    {
        var s = App.Settings.Settings;
        s.FloatingScale = scale;
        s.FloatingOpacity = opacity;
        ApplySettings();
    }

    // ------------------------------------------------------------------
    // 设置
    // ------------------------------------------------------------------

    public void ApplySettings()
    {
        var s = App.Settings.Settings;

        // 「小鲸鱼动画」开关或者「形象」翻动了都要重建播放器 ——
        // 两者是同一个毛病：忘了重建，界面就停在旧素材上（关掉之后还在眨、换了形象还是旧鲸鱼）。
        if (!_animationsSettingKnown ||
            _animationsSettingApplied != s.FloatingAnimations ||
            !_characterSettingKnown ||
            !string.Equals(_characterSettingApplied, s.FloatingCharacter ?? "", StringComparison.Ordinal))
        {
            ReloadAnimations();
        }

        Opacity = s.FloatingOpacity;

        if (!_collapsed) ApplySize();

        if (IsLoaded)
        {
            if (s.FloatingAutoCollapse && !IsMouseOver && !_hasBubble) Collapse();
            else Expand();
        }
    }

    /// <summary>
    /// 重新套用外部素材（用户在 Assets\ 里换了图之后用）。
    ///
    /// 取不到就保留原图 —— 绝不把 Source 赋成 null，否则鲸鱼会凭空消失，
    /// 用户还以为是自己那张图把程序搞坏了。
    /// </summary>
    public void ReloadAssets()
    {
        // 素材缓存必须先清 —— 不清的话用户刚换的那张图根本读不到（还是旧图）
        IconAssets.Reload();

        var head = IconAssets.Load("whale-head");
        if (head != null) HeadImage.Source = head;

        // 鲸鱼本体、悬浮态差分、动态素材都在这一处重扫：用户刚把 sheet.png / sheet.json
        // 或差分图拷进来，就该立刻生效。鲸鱼图贴哪一张由它按"此刻是不是悬停"决定，
        // 所以这里不再直接给 WhaleImage.Source 赋值（否则悬停状态会被这一下覆盖掉）。
        ReloadAnimations();
    }

    /// <summary>
    /// 拖动滑杆时实时预览，不落盘。
    ///
    /// 落盘走的是 700ms 的节流，如果连界面效果也等它，拖动过程中鲸鱼会纹丝不动，
    /// 看上去就是"这个设置没用"。所以拖动时直接套用，节流只负责最后写文件。
    /// </summary>
    public void PreviewScaleAndOpacity(double scale, double opacity)
    {
        Opacity = opacity;

        // 收起态的小胶囊尺寸做过收窄，拖动时先不动它，展开时会自动跟上
        if (_collapsed) return;

        var (w, h) = SizeFor(collapsed: false, scaleOverride: scale);
        ResizeAnchored(w, h);
    }

    /// <summary>
    /// 按当前状态和「大小」设置重新排尺寸。
    ///
    /// 关键：窗口尺寸和内容缩放必须出自同一个系数。
    /// 之前只改窗口宽高，内容的尺寸是写死的（鲸鱼恒为 168px、状态条固定、字号固定），
    /// 于是放大时窗口大了内容不变（一片空），缩小时内容撑不下就被裁掉十来个像素。
    /// 现在用 LayoutTransform 把内容整体等比缩放，两者永远一致 —— 不存在"尺寸对不上"的中间态。
    /// </summary>
    private void ApplySize(bool? collapsed = null)
    {
        var (w, h) = SizeFor(collapsed ?? _collapsed);
        ResizeAnchored(w, h);
    }

    /// <summary>
    /// 算出某个状态该有的窗口尺寸，同时把内容缩放系数同步好。
    /// 尺寸和缩放系数只在这里算，别的地方直接改 Width/Height 就会出现"窗口变了内容没变"。
    /// </summary>
    private (double W, double H) SizeFor(bool collapsed, double? scaleOverride = null)
    {
        var s = App.Settings.Settings;

        // 收起态的小胶囊不做等比：0.7 倍会小得点不着，1.6 倍又太占地方，收窄到 0.8~1.2
        double factor = collapsed
            ? Math.Clamp(scaleOverride ?? s.FloatingScale, 0.8, 1.2)
            : Math.Clamp(scaleOverride ?? s.FloatingScale, AppSettings.MinFloatingScale, AppSettings.MaxFloatingScale);

        double designW = collapsed ? CollapsedSize : ExpandedDesignWidth;
        double designH = collapsed ? CollapsedSize : ExpandedDesignHeight;

        double w = Math.Round(designW * factor);
        double h = Math.Round(designH * factor);

        // 用取整后的实际尺寸反算缩放系数，保证内容和窗口严丝合缝
        _contentScale.ScaleX = w / designW;
        _contentScale.ScaleY = h / designH;

        return (w, h);
    }

    private void HookDataChanges()
    {
        App.Store.Changed += RefreshStatus;
        App.Reminder.Ticked += RefreshStatus;
        App.Settings.Changed += ApplySettings;
    }

    protected override void OnClosed(EventArgs e)
    {
        App.Store.Changed -= RefreshStatus;
        App.Reminder.Ticked -= RefreshStatus;
        App.Settings.Changed -= ApplySettings;
        base.OnClosed(e);
    }

    // ------------------------------------------------------------------
    // 位置与贴边
    // ------------------------------------------------------------------

    private void RestorePosition()
    {
        var s = App.Settings.Settings;

        // 尺寸和内容缩放一起算 —— 启动时如果只设 Width/Height，
        // 保存的缩放值（比如 0.7）就会让内容撑出窗口，一开机就是裁切状态。
        var (w, h) = SizeFor(collapsed: false);

        var area = CurrentWorkArea();

        if (double.IsNaN(s.FloatingLeft) || double.IsNaN(s.FloatingTop))
        {
            // 首次运行：贴右边，纵向放在偏上位置，不挡任务栏
            Left = area.Right - w - 8;
            Top = area.Top + area.Height * 0.52;
        }
        else
        {
            Left = s.FloatingLeft;
            Top = s.FloatingTop;
        }

        Width = w;
        Height = h;
        _edge = NearestEdge();
        ClampToArea();
        _positioned = true;
    }

    /// <summary>当前窗口所在显示器的工作区（已换算成 WPF 的 DIP 单位）。</summary>
    private Rect CurrentWorkArea()
    {
        try
        {
            var topLeft = PointToScreen(new Point(0, 0));
            var screen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point((int)topLeft.X, (int)topLeft.Y));
            var wa = screen.WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Rect(
                wa.Left / dpi.DpiScaleX,
                wa.Top / dpi.DpiScaleY,
                wa.Width / dpi.DpiScaleX,
                wa.Height / dpi.DpiScaleY);
        }
        catch
        {
            // 换算失败就退回主显示器，至少不会把窗口丢到屏幕外
            return SystemParameters.WorkArea;
        }
    }

    private void ClampToArea()
    {
        if (BackgroundProbeMode) return;   // 探针窗口就该待在屏幕外

        var a = CurrentWorkArea();
        if (Left < a.Left) Left = a.Left;
        if (Top < a.Top) Top = a.Top;
        if (Left + Width > a.Right) Left = Math.Max(a.Left, a.Right - Width);
        if (Top + Height > a.Bottom) Top = Math.Max(a.Top, a.Bottom - Height);
    }

    /// <summary>改变窗口尺寸时保持贴靠的那条边不动。</summary>
    private void ResizeAnchored(double w, double h)
    {
        double oldRight = Left + Width;
        double oldBottom = Top + Height;

        double newLeft = Left;
        double newTop = Top;

        switch (_edge)
        {
            case Edge.Right: newLeft = oldRight - w; break;
            case Edge.Left: newLeft = Left; break;
            case Edge.Top: newTop = Top; break;
            case Edge.Bottom: newTop = oldBottom - h; break;
        }

        Width = w;
        Height = h;
        Left = newLeft;
        Top = newTop;
        ClampToArea();
    }

    private Edge NearestEdge()
    {
        var a = CurrentWorkArea();
        double dLeft = Left - a.Left;
        double dRight = a.Right - (Left + Width);
        double dTop = Top - a.Top;
        double dBottom = a.Bottom - (Top + Height);

        double min = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));

        if (min > SnapThreshold) return Edge.None;
        if (min == dLeft) return Edge.Left;
        if (min == dRight) return Edge.Right;
        if (min == dTop) return Edge.Top;
        return Edge.Bottom;
    }

    private void SnapToEdge()
    {
        var a = CurrentWorkArea();

        double dLeft = Left - a.Left;
        double dRight = a.Right - (Left + Width);
        double dTop = Top - a.Top;
        double dBottom = a.Bottom - (Top + Height);

        if (App.Settings.Settings.FloatingSnapToEdge)
        {
            // 两个轴各判各的。早先的写法是"四条边里挑最近的一条"，于是拖到角落里
            // 只贴一条边 —— 看着就是"贴着贴着又偏出去一截"。两轴独立之后自然吸角。
            bool snapX = Math.Min(dLeft, dRight) <= SnapThreshold;
            bool snapY = Math.Min(dTop, dBottom) <= SnapThreshold;

            bool toLeft = dLeft <= dRight;
            bool toTop = dTop <= dBottom;

            if (snapX) Left = toLeft ? a.Left : a.Right - Width;
            if (snapY) Top = toTop ? a.Top : a.Bottom - Height;

            if (snapX || snapY)
            {
                // 锚定边取贴住的那几条里更近的一条：收起 / 展开时靠它决定哪条边不动，
                // 不然每次变尺寸都能看到窗口"挪"一下。
                _edge = PickEdge(
                    snapX ? (toLeft ? Edge.Left : Edge.Right) : Edge.None, Math.Min(dLeft, dRight),
                    snapY ? (toTop ? Edge.Top : Edge.Bottom) : Edge.None, Math.Min(dTop, dBottom));

                ClampToArea();
                return;
            }
        }

        // 没进吸附范围（或者用户关掉了吸附）：仍然记下离哪条边最近，
        // 后续收起 / 展开才有明确方向 —— 否则会按"四边都不靠"处理，一收就四边乱动。
        _edge = NearestEdge();
        if (_edge == Edge.None)
        {
            double min = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));
            _edge = min == dLeft ? Edge.Left
                 : min == dRight ? Edge.Right
                 : min == dTop ? Edge.Top
                 : Edge.Bottom;
        }

        ClampToArea();
    }

    /// <summary>两条边都贴住时取更近的那条当锚定边；只贴了一条就用那一条。</summary>
    private static Edge PickEdge(Edge x, double dx, Edge y, double dy)
    {
        if (x == Edge.None) return y;
        if (y == Edge.None) return x;
        return dx <= dy ? x : y;
    }

    private void SavePosition()
    {
        var left = Left;
        var top = Top;
        App.Settings.Update(s =>
        {
            s.FloatingLeft = left;
            s.FloatingTop = top;
        });
    }

    // ------------------------------------------------------------------
    // 收起 / 展开
    // ------------------------------------------------------------------

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        Expand();
        SetHovering(true);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // 先还原形象，再决定要不要排「自动收起」。顺序不能反 ——
        // 下面那个分支会提前 return，把形象切换写在它后面的话，
        // 关掉了自动收起的用户鼠标移开后就永远回不到本体形象了。
        SetHovering(false);

        if (!App.Settings.Settings.FloatingAutoCollapse) return;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    /// <summary>
    /// 切换悬浮态形象（表情差分）。
    ///
    /// 没配悬浮图时 <see cref="RestingImage"/> 会退回本体，这个函数就等于"把本体再贴一遍"，
    /// 不会出错 —— 所以调用方不需要先判断有没有配图。
    ///
    /// 正在播动画时不抢 Source：抢了会把动作截断成半张脸。播放器播完会自己落回
    /// <see cref="SpritePlayer.BaseImage"/>，而下面已经把它一起切过去了，
    /// 所以不会出现"悬停期间播完一次眨眼、落回睁眼"的跳变。
    /// </summary>
    private void SetHovering(bool hovering)
    {
        if (_hovering == hovering) return;
        _hovering = hovering;

        // 播完要落回的那张也得跟着切
        if (_player != null) _player.BaseImage = RestingImage;

        // 悬停期间提前把"点击回应"备好（没移开就一直是备着的），
        // 移开就收掉 —— 别让一个 ffmpeg 进程为了一个没发生的点击常驻。
        if (hovering) WarmClick();
        else _player?.ReleaseWarm();

        if (_player is { IsPlaying: true }) return;
        ApplyRestingImage();
    }

    /// <summary>
    /// 把「此刻该显示的静止图」贴到鲸鱼上。
    /// 取不到就保持原样 —— 绝不把 Source 置成 null，否则鲸鱼会凭空消失。
    /// </summary>
    private void ApplyRestingImage()
    {
        var resting = RestingImage;
        if (resting == null) return;
        if (ReferenceEquals(WhaleImage.Source, resting)) return;

        WhaleImage.Source = resting;
    }

    public void Collapse()
    {
        if (_collapsed || !_ready) return;

        _collapsed = true;
        ExpandedRoot.Visibility = Visibility.Collapsed;
        CollapsedRoot.Visibility = Visibility.Visible;

        // 收起了就没人看动画了：停掉播放器（顺手杀掉 ffmpeg）和闲置排期，
        // 不然一个收成小胶囊的窗口还在后台一直解码、白烧 CPU
        StopAnimations();

        ApplySize();
        SavePosition();
        RefreshStatus();
    }

    public void Expand()
    {
        if (!_collapsed || !_ready)
        {
            if (!_collapsed) RefreshStatus();
            return;
        }

        _collapsed = false;
        ExpandedRoot.Visibility = Visibility.Visible;
        CollapsedRoot.Visibility = Visibility.Collapsed;

        ApplySize();
        SavePosition();
        RefreshStatus();

        StartIdleLoop();
        ScheduleAmbient();
    }

    /// <summary>停掉所有动画活动（播放器 + 眨眼排期 + 闲置排期 + 预热 + 没演完的表演）。</summary>
    private void StopAnimations()
    {
        _blinkTimer?.Stop();
        _ambientTimer?.Stop();

        // 表演的剩余段落也作废：窗口收起（或不可见）时它已经没法演了，
        // 留着的话下次展开会莫名其妙地接着演"雀跃庆祝"。
        CancelCelebration();

        _player?.Stop();
        _player?.ReleaseWarm();
    }

    private void ToggleCollapse()
    {
        if (_collapsed) Expand();
        else Collapse();
    }

    // ------------------------------------------------------------------
    // 拖拽
    // ------------------------------------------------------------------

    /// <summary>
    /// 后台探针模式：不把窗口拉回工作区。
    ///
    /// 诊断自检和截图要在屏幕外创建一个真实的悬浮窗来量尺寸/出图，但
    /// <see cref="ClampToArea"/> 会把任何越界的位置强行拽回屏幕内（于是"摆到 -32000"
    /// 实际会停在桌面左上角）。开着这个开关就跳过约束，窗口老老实实待在屏幕外。
    /// 默认 false，正常运行行为完全不变。
    /// </summary>
    public bool BackgroundProbeMode { get; set; }

    /// <summary>
    /// 判定"拖拽还是点击"的位移阈值，单位是 DIP。
    /// 用 DIP 而不是物理像素：150% 缩放下 5 物理像素只有 3.3 DIP，手稍微一抖点击就没了。
    /// </summary>
    private const double ClickSlopDip = 5;

    private void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _collapseTimer.Stop();

        // 记屏幕坐标，而不是窗口内坐标：窗口会跟着鼠标一起走，
        // 鼠标相对窗口的位置几乎不变，用它算位移永远是 0 —— 这正是
        // "不管拖多久都被当成点击"的第二层原因。
        _dragStartCursor = System.Windows.Forms.Cursor.Position;
        _widgetLeftAtPress = Left;
        _widgetTopAtPress = Top;

        _dragging = true;

        // 鼠标一按下去，正在演的归档表演就交还控制权 —— 用户要摸它、要拖它，
        // 表演的剩余段落作废（这一下之后是点击弹跳或拖拽动作，不该再续演庆祝）。
        CancelCelebration();

        ScheduleDragAnimation();

        try
        {
            // 阻塞式：返回时鼠标已经松开。
            //
            // 注意 WM_LBUTTONUP 是在这个模态移动循环里派发的 —— WPF 会在循环内部
            // 就抛出 MouseLeftButtonUp，**先于**这里后面的代码执行。所以判定不能在
            // 后面算完再等 Up 来读，必须在 OnLeftButtonUp 里独立算一遍。
            //
            // DragMove 是个嵌套消息循环，不是"界面冻住"：渲染时钟和 DispatcherTimer
            // 在拖动全程照常跑，所以被拖着的那套动作是真的在放。
            DragMove();
        }
        catch
        {
            // 鼠标状态异常（比如拖拽中按了 Esc）会抛，忽略即可
        }

        _dragging = false;
        _dragTimer?.Stop();

        var dragged = HasMovedSincePress();

        if (dragged)
        {
            SnapToEdge();
            SavePosition();
        }

        ResumeAfterDrag(dragged);
    }

    /// <summary>
    /// 拖拽动作延迟一小会儿才起播。
    ///
    /// 不能一按下就播：视频动作每播一次都要起一个 ffmpeg（约 150 ms），
    /// 而"点一下"根本不拖动 —— 那一趟进程启动纯属白花，还会跟点击弹跳
    /// 抢着换动作（看起来就是闪了一下）。延迟 180 ms，真的拖起来了才播。
    /// </summary>
    private void ScheduleDragAnimation()
    {
        if (_dragActions.Count == 0) return;

        _dragTimer ??= CreateDragTimer();
        _dragTimer.Stop();
        _dragTimer.Start();
    }

    private DispatcherTimer CreateDragTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_dragging) PlayRandomOfRole(_dragActions);
        };

        return timer;
    }

    /// <summary>
    /// 拖拽结束后的收尾。
    ///
    /// 分两种情况，差别很要紧：
    ///   · 真的拖动过 → 把拖拽动作收掉，回到待机循环（或者按设置收起）。
    ///   · 只是点了一下 → **什么都别做**。那一下的"点击弹跳"是在 MouseUp 里
    ///     刚起播的，这时候去 Stop 就会把反馈切掉；弹跳播完会自己接回待机。
    /// </summary>
    private void ResumeAfterDrag(bool dragged)
    {
        if (!dragged) return;

        if (App.Settings.Settings.FloatingAutoCollapse)
        {
            Collapse();      // Collapse 里已经把动画停了
            return;
        }

        if (_idleActions.Count == 0)
        {
            _player?.Stop();     // 没有待机素材（原版那套）：拖拽动作停掉，回静态形象
            return;
        }

        _loopingIdle ??= _idleActions[BlinkRng.Next(_idleActions.Count)];

        if (_player != null && (!_player.IsPlaying || _player.CurrentAction != _loopingIdle))
            _player.Play(_loopingIdle);
    }

    /// <summary>
    /// 从按下到现在，鼠标或窗口是否真的动过。
    ///
    /// 两个判据取"或"：光标移动了，或者窗口真的被挪了。
    /// 后者还兜住了"拖出去又拖回原位"——那种情况下光标位移是 0，但窗口确实被拖动过。
    /// </summary>
    private bool HasMovedSincePress()
    {
        var now = System.Windows.Forms.Cursor.Position;
        int slop = SlopInDevicePixels();

        bool cursorMoved = Math.Abs(now.X - _dragStartCursor.X) > slop
                        || Math.Abs(now.Y - _dragStartCursor.Y) > slop;

        bool windowMoved = Math.Abs(Left - _widgetLeftAtPress) > 0.5
                        || Math.Abs(Top - _widgetTopAtPress) > 0.5;

        return cursorMoved || windowMoved;
    }

    /// <summary>把 DIP 阈值换算成当前屏幕的物理像素。</summary>
    private int SlopInDevicePixels()
    {
        double dpiScale = 1.0;
        try
        {
            dpiScale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        }
        catch
        {
            // 拿不到就用 100%，比不换算好
        }

        return Math.Max(2, (int)Math.Round(ClickSlopDip * dpiScale));
    }

    private void OnLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 拖动过就不算点击。这个判定必须在这里算：拖拽时本事件是在 DragMove 的
        // 模态循环里派发的，那时 OnLeftButtonDown 后半段还没跑，任何"事后"才算出来的
        // 标志位都还是旧值（早期版本就是这样，拖多久都会被当成点击打开主面板）。
        if (HasMovedSincePress()) return;

        // 状态条上的单击用于手动收起/展开，鲸鱼身上的单击打开主面板
        if (ExpandedRoot.IsVisible && e.OriginalSource is DependencyObject src &&
            IsDescendantOf(src, CountdownText))
        {
            ToggleCollapse();
            return;
        }

        // 戳一下鲸鱼先弹一跳：主面板要等一下才画出来，这一跳是"点到了"的即时反馈。
        // 素材里有多个点击动作用随机挑一个；一个都没有就退回按老名字找，
        // 再没有 PlayAnimation 返回 false，行为完全等于以前。
        if (!PlayRandomOfRole(_clickActions)) PlayAnimation(AnimActions.Click);

        ((App)Application.Current).ShowMainWindow();
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    // ------------------------------------------------------------------
    // 文件拖放（把文件丢到小鲸鱼上 → 归档并打开主面板）
    // ------------------------------------------------------------------

    // 拖放和上面的"拖动窗口"不会互相误判：外部文件拖进来走的是 OLE 拖放，
    // 鼠标按钮确实按着，但按下那一刻发生在资源管理器里，这个窗口根本收不到
    // MouseLeftButtonDown —— 所以不会走进 OnLeftButtonDown 的窗口拖动分支。

    private static bool HasFiles(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop);

    private void OnFileDragEnter(object sender, DragEventArgs e)
    {
        if (!HasFiles(e.Data)) return;

        e.Effects = DragDropEffects.Copy;
        ShowDropHint(CountFiles(e.Data));
        e.Handled = true;
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        if (!HasFiles(e.Data)) return;

        // 拖动全程系统会反复问，每次都要重新表态一次 Effect ——
        // 漏一次光标就变回"禁止"图标。顺手兜一下提示层：万一 DragLeave 判早了
        // （见 OnFileDragLeave），下一次 DragOver 会把它重新点亮。
        e.Effects = DragDropEffects.Copy;
        if (DropHint.Visibility != Visibility.Visible) ShowDropHint(CountFiles(e.Data));
        e.Handled = true;
    }

    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave 在鼠标掠过**任何子元素**边界时都会冒上来（鲸鱼图、状态条、
        // 角标各算一个），无脑隐藏会让提示层一闪一闪。用相对坐标判断是不是真的离开了窗口。
        var pos = e.GetPosition(this);
        bool outside = pos.X < 0 || pos.Y < 0 || pos.X > ActualWidth || pos.Y > ActualHeight;
        if (outside) HideDropHint();
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        HideDropHint();
        if (!HasFiles(e.Data)) return;

        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        await AcceptDropAsync(paths);
    }

    private static int CountFiles(IDataObject data)
    {
        try { return data.GetData(DataFormats.FileDrop) is string[] p ? p.Length : 0; }
        catch { return 0; }
    }

    /// <summary>
    /// 收到文件之后的全部动作：归档 → （按设置）打开主面板 → 弹气泡报结果。
    ///
    /// 单独抽出来是为了让自检能绕过 OLE 事件层直接调它 —— <see cref="DragEventArgs"/>
    /// 的构造函数是 internal，测试里造不出来，能覆盖的边界只能划在这里。
    /// "系统到底有没有把拖放交到这个窗口上"由 tools/drag_probe.py 做真实拖放来验。
    ///
    /// 两条路的**归档行为必须一模一样**（都走 FileIntakeService、都记入今天），
    /// 差别只有"要不要把主面板叫出来" —— 设置项的名字说的正是这件事，
    /// 别让它只掐掉半个动作（那会变成"面板照样弹出来、只是没切页"，与开关的承诺不符）。
    ///
    /// 归档成功（真的收下了新东西）之后演一段「清点归档 → 雀跃庆祝」，
    /// 见 <see cref="AnimActions.Celebrate"/>。两条路都会演 ——
    /// 用户关掉的是"弹不弹面板"，不是"小鲸鱼别理我"。
    /// </summary>
    public async Task<string> AcceptDropAsync(string[] paths)
    {
        // 先动一下。主面板从零创建要几百毫秒，这段空档界面上必须有点反应，
        // 否则用户以为没接住、会再拖一次。没有点击素材时这个调用什么都不做，
        // 靠提示层收掉 + 下面那个气泡也够了。
        PlayRandomOfRole(_clickActions);

        var app = (App)Application.Current;

        if (!App.Settings.Settings.AutoOpenMainWindowOnDrop)
        {
            // 用户关了「自动打开主面板」：安静地收下，只用气泡回报。
            var quiet = await app.ArchiveFromFloatingQuietlyAsync(paths);
            CelebrateArchived(quiet);
            ShowBubble("已归档", quiet.Text);
            return quiet.Text;
        }

        var main = await app.ShowMainWindowForDropAsync();

        if (main == null)
        {
            const string failed = "主面板打不开，文件没有归档";
            ShowBubble("没能归档", failed, urgency: 1);
            return failed;
        }

        var outcome = await main.IntakeDroppedAsync(paths);
        CelebrateArchived(outcome);
        ShowBubble("已归档", outcome.Text);
        return outcome.Text;
    }

    /// <summary>
    /// 真的收下了东西才演一段庆祝。
    ///
    /// 判据是"新增 + 新版本 > 0"，不是"文件收下了"：把同一份文件再拖一次
    /// （重复 / 内容未变）时，小鲸鱼不该蹦起来庆祝 —— 那是"没什么值得庆祝的"，
    /// 看着像在敷衍。气泡照旧会老实说"重复 1"，用户不会觉得没反应。
    /// </summary>
    private void CelebrateArchived(FileIntakeService.IntakeOutcome outcome)
    {
        if (!outcome.HasNews) return;
        PlayCelebration();
    }

    /// <summary>拖进来时把落点提示亮出来。</summary>
    private void ShowDropHint(int fileCount)
    {
        // 收起态那个小胶囊（60px）里塞不下图标和两行字，只留高亮框。
        // 也别为此把它展开：拖拽中途改窗口尺寸会让光标相对位置漂移，拖拽会抖。
        bool room = !_collapsed;

        DropHintIcon.Visibility = room ? Visibility.Visible : Visibility.Collapsed;
        DropHintText.Visibility = room ? Visibility.Visible : Visibility.Collapsed;
        DropHintText.Text = fileCount > 1 ? $"松手归档\n{fileCount} 个文件" : "松手归档";

        DropHint.Visibility = Visibility.Visible;
    }

    private void HideDropHint() => DropHint.Visibility = Visibility.Collapsed;

    // ---- 拖放（诊断自检入口）----

    /// <summary>自检用：落点提示此刻亮着没有。</summary>
    public bool DropHintVisible => DropHint.Visibility == Visibility.Visible;

    /// <summary>自检用：落点提示上的字。</summary>
    public string DropHintLabel => DropHintText.Text;

    /// <summary>自检用：走一遍真实的"拖进来"，验证提示层真的会亮。</summary>
    public void ShowDropHintForProbe(int fileCount) => ShowDropHint(fileCount);

    /// <summary>自检用：走一遍真实的"拖走了"。</summary>
    public void HideDropHintForProbe() => HideDropHint();

    // ------------------------------------------------------------------
    // 动态素材（精灵图动画）
    // ------------------------------------------------------------------

    /// <summary>
    /// 按设置和磁盘上的素材重建动画播放器。可重复调用（「重新加载素材」和开关翻动都走它）。
    ///
    /// 整段是"尽力而为"的：任何一环出问题都退回静态形象 ——
    /// 素材是用户可以随手替换的东西，它不该有把悬浮窗搞挂的能力。
    /// </summary>
    private void ReloadAnimations()
    {
        var wanted = App.Settings.Settings.FloatingAnimations;
        _animationsSettingApplied = wanted;
        _animationsSettingKnown = true;

        // 形象：设置里那个能用就用，不能就降级（dsh → 原版）。原版永远兜底，
        // 所以这里永远不会返回一个用不了的形象 —— 素材被删了、或者从旧版本
        // 升级上来（配置里没这个字段），界面都不会空。
        _characterSettingApplied = App.Settings.Settings.FloatingCharacter ?? "";
        _characterSettingKnown = true;
        _characterId = CharacterPacks.Resolve(_characterSettingApplied);
        _characterRoot = CharacterPacks.DirOf(_characterId);

        // 形象包自己的画布几何。**必须在算显示框之前读** —— 它就是"这套模板该显示多大"的答案。
        _geometry = CharacterGeometry.Resolve(_characterRoot);

        try
        {
            _blinkTimer?.Stop();
            _ambientTimer?.Stop();
            DetachPlayer();
            ResetRoleLists();

            _anim = AnimLibrary.Scan(_characterRoot);

            // 静态图先取出来：无论开不开动画，它都是"没在播"时的底图。
            // anim\base.png 也在素材清单里，取到了它就是这张。
            _stillImage = IconAssets.Load("whale", _characterRoot) as BitmapSource;

            // 悬浮差分只在原版形象上允许回落到内置那张（闭眼）。新形象没有差分时
            // 回落成原版闭眼图，效果就是「换了形象，鼠标一移上去变回旧鲸鱼」——
            // 明显是错的，所以新形象只认自己目录里的图，没有就干脆不悬浮变化。
            _hoverImage = IsClassicRoot
                ? IconAssets.Load("whale-hover", _characterRoot) as BitmapSource
                : IconAssets.LoadPackOnly("whale-hover", _characterRoot) as BitmapSource;

            // 悬浮态要立刻生效 —— 重扫素材时鼠标可能正停在鲸鱼上
            var resting = RestingImage;
            if (resting != null) WhaleImage.Source = resting;

            _animationsOn = wanted && _anim.HasAny;

            // 显示框：静止用形象包声明的几何，动起来再和动作素材要的框取大者。
            // 两者出自同一套换算（角色 165px 高），所以"静止 ↔ 播放"不会跳大小 ——
            // 2026-09-20 之前是"静态用写死的 168、动画用帧表算的 234.7"，
            // dsh 每播一次动作就放大一倍再缩回去。
            RecomputeWhaleBox();

            if (_animationsOn)
            {
                _player = new SpritePlayer(WhaleImage) { BaseImage = resting };

                foreach (var action in _anim.ActionNames)
                {
                    var sheet = _anim.Find(action);
                    var path = _anim.MediaPath(action);
                    if (sheet != null && path != null) _player.Register(action, sheet, path);
                }

                _player.Finished += OnAnimationFinished;

                // 按角色分好类：播的时候只管挑，不再去猜哪个动作是干什么的
                _idleActions = _anim.ActionsWithRole(AnimActions.Idle);
                _clickActions = _anim.ActionsWithRole(AnimActions.Click);
                _ambientActions = _anim.ActionsWithRole(AnimActions.Ambient);
                _dragActions = _anim.ActionsWithRole(AnimActions.Drag);
                // 成套的表演：按目录名排好的顺序（celebrate-1-… → celebrate-2-…），
                // 界面逐段播，不随机挑（见 AnimActions.Celebrate）
                _celebrateActions = _anim.ActionsWithRole(AnimActions.Celebrate);
            }

            ApplyWhaleBox();

            // 只有位置已经摆过才能重排尺寸 —— 首次 Loaded 时 Left/Top 还是 NaN，
            // 这时候走 ResizeAnchored 会把窗口摆到 NaN 上去。
            if (_ready && _positioned) ApplySize();

            StartIdleLoop();
            ScheduleBlink();
            ScheduleAmbient();
        }
        catch (Exception ex)
        {
            CrashLog.Note($"[动画] 初始化失败，退回静态形象：{ex}");

            _animationsOn = false;
            RecomputeWhaleBox();
            ApplyWhaleBox();
            DetachPlayer();
            ResetRoleLists();
        }
    }

    /// <summary>
    /// 重算显示框。**两套形象各算各的**（"不要统一高度"就落在这儿）：
    ///
    ///     框 = 形象包声明的几何 ⊕ 动作素材帧表要的框   （逐轴取大者）
    ///
    /// 取大者而不是"用后一个"：静止时看的是形象包几何，动起来看的是帧表，
    /// 谁大用谁 —— 按小的那个算，大的那个就会在播放时被裁。
    ///
    /// 帧表没写 characterHeight 时它算不出框，此时就老实按形象包几何显示
    /// （老行为是"退回静态尺寸"，那会把 dsh 那套挤回 168，正是故障本身）。
    /// </summary>
    private void RecomputeWhaleBox()
    {
        var (w, h) = _geometry.IsValid
            ? _geometry.DisplayBox()
            : CharacterGeometry.Classic.DisplayBox();

        if (_animationsOn && _anim != null)
        {
            var wanted = _anim.DisplayBox(CharacterGeometry.DesignCharacterHeight);
            if (wanted.HasValue)
            {
                w = Math.Max(w, wanted.Value.Width);
                h = Math.Max(h, wanted.Value.Height);
            }
        }

        _whaleBox = (Math.Clamp(w, MinWhaleBox, MaxWhaleBox),
                     Math.Clamp(h, MinWhaleBox, MaxWhaleBox));
    }

    /// <summary>把显示框贴到鲸鱼图上。</summary>
    private void ApplyWhaleBox()
    {
        // 只设高度：宽度由 WPF 按图片自身的比例算（Stretch=Uniform），
        // 而画布比例就是框的比例，所以算出来正好等于框宽 —— 视频和静态图都一样。
        // 设死宽度反而会出事：两者比例一旦对不上就会被 Uniform 加上黑边。
        WhaleImage.Height = _whaleBox.H;
        WhaleImage.Width = double.NaN;
    }

    private void ResetRoleLists()
    {
        _idleActions = Array.Empty<string>();
        _clickActions = Array.Empty<string>();
        _ambientActions = Array.Empty<string>();
        _dragActions = Array.Empty<string>();
        _celebrateActions = Array.Empty<string>();
        _loopingIdle = null;

        // 换形象 / 关动画时，上一套素材的表演段落不能再留着 ——
        // 留着的话"演到一半切了形象"，接力会去播新素材里同名的动作
        // （没有就静默停住），而队列里那几个名字已经作废了。
        CancelCelebration();
    }

    /// <summary>
    /// 当前形象就是原版（素材根目录 = 素材根本身）。
    ///
    /// 只有这种情形才允许把内置的闭眼差分当兜底 —— 内置那张画的是原版鲸鱼娘，
    /// 拿它去配新形象就是穿帮。
    /// </summary>
    private bool IsClassicRoot
    {
        get
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(_characterRoot),
                    Path.GetFullPath(AppPaths.AssetsDir),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }
    }

    // ------------------------------------------------------------------
    // 待机循环 / 闲置小动作
    // ------------------------------------------------------------------

    /// <summary>
    /// 此刻该不该有"待机循环"：开着动画、没收起、窗口可见、也没在拖。
    ///
    /// 刻意**不排除鼠标悬停** —— 待机是"呼吸"这种一直在动的东西，
    /// 鼠标扫过去就定格会很怪。而眨眼那种一次性的小动作仍然避开悬停
    /// （见 <see cref="CanBlinkNow"/>），不然像在偷看。
    /// </summary>
    private bool WantsIdleLoop
        => _animationsOn && _player != null && !_collapsed && IsVisible && !_dragging;

    /// <summary>此刻能不能自己冒个动作出来（闲置小动作用）。鼠标停在身上、或者正弹气泡时不冒。</summary>
    private bool CanActFreely => WantsIdleLoop && !IsMouseOver && !_hasBubble;

    /// <summary>
    /// 开始（或恢复）待机循环。
    ///
    /// 这是"背景状态"：素材里有 idle 动作时，小鲸鱼平时一直循环播它；
    /// 点击 / 闲置小动作 / 提醒这些一次性的动作播完之后，回到这里。
    /// 没有 idle 素材（原版那套只有眨眼）时什么都不做 —— 退回静态形象 + 眨眼，
    /// 行为和加这个功能之前完全一样。
    /// </summary>
    private void StartIdleLoop()
    {
        if (!WantsIdleLoop) return;
        if (_player!.IsPlaying) return;
        if (_idleActions.Count == 0) return;

        _loopingIdle ??= _idleActions[BlinkRng.Next(_idleActions.Count)];
        _player.Play(_loopingIdle);
    }

    /// <summary>没在播别的就恢复待机（一次性动作播完的回程）。</summary>
    private void ResumeIdleIfFree()
    {
        if (_player is not { IsPlaying: false }) return;
        StartIdleLoop();
    }

    // ------------------------------------------------------------------
    // 归档表演（文件归档成功后演的成套动作）
    // ------------------------------------------------------------------

    /// <summary>
    /// 演一段"归档表演"：角色 <see cref="AnimActions.Celebrate"/> 下的动作**按顺序**播，
    /// 上一段播完由 <see cref="OnAnimationFinished"/> 接着往下接力。
    ///
    /// 几个刻意的选择：
    ///
    ///   · **只在正经站着的时候演**（<see cref="WantsIdleLoop"/>）：收起态、拖拽中、
    ///     窗口不可见时都不演。用户正把窗口拖来拖去的时候突然蹦起来，是"失控"而不是"活泼"。
    ///   · **单段播不了就跳过，不整套哑掉**：素材少一段、某段视频坏了，
    ///     能演的照演 —— 归档成功的反馈不该因为一段素材坏了就整个消失。
    ///   · **从头开始**：连着拖两批文件时，第二批会让表演重来一遍
    ///     （而不是把上一轮的尾巴续上 —— 那两批文件就对不上了）。
    /// </summary>
    private bool PlayCelebration()
    {
        if (!WantsIdleLoop) return false;
        if (_celebrateActions.Count == 0) return false;

        for (int i = 0; i < _celebrateActions.Count; i++)
        {
            if (!PlayAnimation(_celebrateActions[i])) continue;

            _celebrateCurrent = _celebrateActions[i];
            _celebrateQueue.Clear();
            for (int j = i + 1; j < _celebrateActions.Count; j++) _celebrateQueue.Add(_celebrateActions[j]);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 归档表演的接力：上一段正常播完就接着演下一段。
    /// 返回 true 表示"接下来还有得演，别急着回待机"。
    /// </summary>
    private bool ContinueCelebration(string finishedAction)
    {
        if (_celebrateQueue.Count == 0)
        {
            // 演完了（或者压根没在演）。清掉"正在演的那一段"，让下次判断回到干净状态。
            _celebrateCurrent = null;
            return false;
        }

        // 播完的不是表演里的这一段 → 说明中间被打断了（用户点了一下、拖了一下，
        // 或者别的动作抢了播放器）。表演就此作罢，剩下的段落作废 ——
        // 否则会在用户那次点击结束时莫名其妙地接上"雀跃庆祝"。
        if (!string.Equals(finishedAction, _celebrateCurrent, StringComparison.Ordinal))
        {
            CancelCelebration();
            return false;
        }

        var next = _celebrateQueue[0];
        _celebrateQueue.RemoveAt(0);

        if (!PlayAnimation(next))
        {
            CancelCelebration();
            return false;
        }

        _celebrateCurrent = next;
        return true;
    }

    /// <summary>把还没演的段落全丢掉（收起窗口、重扫素材、被打断时叫）。</summary>
    private void CancelCelebration()
    {
        _celebrateQueue.Clear();
        _celebrateCurrent = null;
    }

    /// <summary>
    /// 从一堆同角色的动作里随机挑一个播。列表为空返回 false —— 调用方据此走降级分支，
    /// 而不是硬编码假设某个动作一定存在。
    /// </summary>
    private bool PlayRandomOfRole(IReadOnlyList<string> actions)
    {
        if (!_animationsOn || _player == null || actions.Count == 0) return false;

        // 尽量不连着播同一个：两三个动作的素材里，随机挑到重复的概率其实不低
        int index = BlinkRng.Next(actions.Count);
        if (actions.Count > 1 && actions[index] == _player.CurrentAction)
            index = (index + 1) % actions.Count;

        return PlayAnimation(actions[index]);
    }

    /// <summary>安排下一次"自己冒个小动作"。间隔 25~70 秒随机 —— 固定节拍一眼就看得出是循环。</summary>
    private void ScheduleAmbient()
    {
        _ambientTimer ??= CreateAmbientTimer();

        _ambientTimer.Stop();

        if (!_animationsOn || _ambientActions.Count == 0) return;

        _ambientTimer.Interval = TimeSpan.FromSeconds(25 + BlinkRng.NextDouble() * 45);
        _ambientTimer.Start();
    }

    private DispatcherTimer CreateAmbientTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // 鼠标在身上、或者正弹气泡时先不冒 —— 打断用户当下的注意力没礼貌
            if (CanActFreely) PlayRandomOfRole(_ambientActions);

            ScheduleAmbient();
        };

        return timer;
    }

    /// <summary>
    /// 预热"点击回应"的动作。
    ///
    /// 透明视频每次起播都要 spawn 一个 ffmpeg（实测冷启动约 150 ms），
    /// 点击这种要求立刻有反馈的场合能感觉到"卡了一下"。鼠标移进悬浮窗时
    /// 提前把 ffmpeg 起好、头几帧解到手，点下去就是即时的。
    /// </summary>
    private void WarmClick(string? preferred = null)
    {
        if (!_animationsOn || _player == null) return;

        if (_clickActions.Count == 0)
        {
            _player.ReleaseWarm();
            return;
        }

        var target = preferred != null && _clickActions.Contains(preferred)
            ? preferred
            : _clickActions[BlinkRng.Next(_clickActions.Count)];

        _player.Warm(target);
    }

    private void DetachPlayer()
    {
        if (_player == null) return;

        _player.Finished -= OnAnimationFinished;
        _player.Dispose();
        _player = null;
    }

    /// <summary>播一个动作。播不了（没素材 / 素材坏了）返回 false，调用方据此走降级分支。</summary>
    private bool PlayAnimation(string action)
    {
        if (!_animationsOn || _player == null) return false;

        try
        {
            return _player.Play(action);
        }
        catch (Exception ex)
        {
            CrashLog.Note($"[动画] 播放 {action} 失败：{ex.Message}");
            return false;
        }
    }

    private void OnAnimationFinished(string action)
    {
        // 眨完一次就安排下一次 —— 用"播完再排期"而不是固定节拍器，
        // 播放耗时本身也被算进间隔里，节奏不会越走越密
        if (action == AnimActions.Blink) ScheduleBlink();

        // 归档表演的接力排在最前面：它不演完就不该回待机
        // （待机是个循环，先起来的话这一段就永远接不上了）。
        if (!ContinueCelebration(action)) ResumeIdleIfFree();

        // 鼠标还停在鲸鱼上就顺手把点击动作重新预热一份：连着点两下时，
        // 第一下用掉了预热好的那条流，第二下才不用等 ffmpeg 启动
        if (_hovering) WarmClick(action);
    }

    /// <summary>此刻适不适合眨眼：开着动画、展开着、显示着、鼠标不在上面。</summary>
    private bool CanBlinkNow
        => _animationsOn && !_collapsed && IsVisible && !IsMouseOver && !_dragging
           && _player is { IsPlaying: false };

    /// <summary>
    /// 安排下一次眨眼。间隔取 4~9 秒随机 —— 固定节拍一眼就能看出是循环，
    /// 太密集又显得抽搐。没有 blink 这个动作时干脆不排期，免得留个空转的定时器。
    /// </summary>
    private void ScheduleBlink()
    {
        _blinkTimer ??= CreateBlinkTimer();

        _blinkTimer.Stop();

        if (!_animationsOn || _player is not { } p || !p.Has(AnimActions.Blink)) return;

        _blinkTimer.Interval = TimeSpan.FromSeconds(4 + BlinkRng.NextDouble() * 5);
        _blinkTimer.Start();
    }

    private DispatcherTimer CreateBlinkTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // 鼠标在鲸鱼身上、或者正在播别的动作时不打断（Play 的 restart=false 也会兜一层）
            if (CanBlinkNow && _player is { IsPlaying: false }) PlayAnimation(AnimActions.Blink);

            ScheduleBlink();
        };

        return timer;
    }

    // ---- 诊断自检入口 ----

    /// <summary>动态素材当前的状态报告（自检日志里原样写出来）。</summary>
    public string AnimStateReport
    {
        get
        {
            if (_anim == null) return "（动态素材尚未扫描）";

            var head = _animationsOn
                ? $"已启用：形象「{CharacterPacks.TitleOf(_characterId)}」，显示框 {_whaleBox.W:0.#}×{_whaleBox.H:0.#}，" +
                  $"窗口设计 {ExpandedDesignWidth:0.#}×{ExpandedDesignHeight:0.#}，" +
                  $"登记动作 {_player?.RegisteredCount ?? 0} 个" +
                  (_player is { VideoCount: > 0 } ? $"（其中透明视频 {_player.VideoCount} 个）" : "")
                : $"未启用（开关={App.Settings.Settings.FloatingAnimations}，" +
                  $"扫到动作 {_anim.ActionNames.Count} 个）";

            var roles = $"按角色分：待机 {_idleActions.Count}、点击 {_clickActions.Count}、" +
                        $"闲置小动作 {_ambientActions.Count}、拖拽 {_dragActions.Count}、" +
                        $"归档表演 {_celebrateActions.Count}" +
                        (_loopingIdle != null ? $"，当前待机 = {_loopingIdle}" : "");

            // 几何单独列一行：用户在报"形象被裁了/大小不对"时，第一个要看的就是它 ——
            // 框是从哪个画布算出来的、算出来多大，一眼能对上。
            var geo = $"形象几何：{_geometry.Describe()}";

            return head + Environment.NewLine + geo + Environment.NewLine + roles +
                   Environment.NewLine + _anim.Describe();
        }
    }

    /// <summary>自检用：直接拿到播放器验证真实的播放/推进逻辑。</summary>
    public SpritePlayer? AnimPlayer => _player;

    /// <summary>自检用：此刻是否启用了动态素材。</summary>
    public bool AnimationsOn => _animationsOn;

    /// <summary>自检用：显示框的当前高度（设计像素）。</summary>
    public double WhaleHeight => _whaleBox.H;

    /// <summary>自检用：显示框的当前宽度（设计像素）。16:9 的素材靠它才能验"没被挤成方的"。</summary>
    public double WhaleWidth => _whaleBox.W;

    /// <summary>自检用：当前形象包的画布几何。</summary>
    public CharacterGeometry Geometry => _geometry;

    /// <summary>
    /// 自检用：展开态的设计宽高（不含"大小"设置的缩放）。
    ///
    /// 自检断言窗口尺寸时必须拿它当期望值，**不能写死 236** ——
    /// 236 是原版静态形象那一档的版式宽；dsh 那套 16:9 的画布要 391 才装得下，
    /// 写死的话一换形象就叫"窗口尺寸没跟着变"（2026-09-21 改显示框时真踩了）。
    /// </summary>
    public (double W, double H) DesignSize => ExpandedDesignSize;

    // ---- 形象包 ----

    /// <summary>自检用：当前生效的形象 ID。</summary>
    public string CharacterId => _characterId;

    /// <summary>自检用：形象在界面上的名字。</summary>
    public string CharacterTitle => CharacterPacks.TitleOf(_characterId);

    /// <summary>自检用：形象的素材根目录。</summary>
    public string CharacterRoot => _characterRoot;

    /// <summary>自检用：当前形象是不是原版（原版才允许回落到内置闭眼差分）。</summary>
    public bool CharacterIsClassic => IsClassicRoot;

    // ---- 角色动作 ----

    /// <summary>自检用：登记的动作总数。</summary>
    public int RegisteredActionCount => _player?.RegisteredCount ?? 0;

    /// <summary>自检用：登记的动作里有几个是透明视频。</summary>
    public int VideoActionCount => _player?.VideoCount ?? 0;

    public int IdleActionCount => _idleActions.Count;
    public int ClickActionCount => _clickActions.Count;
    public int AmbientActionCount => _ambientActions.Count;
    public int DragActionCount => _dragActions.Count;

    /// <summary>自检用：这个形象包里有几段归档表演（原版是 0）。</summary>
    public int CelebrateActionCount => _celebrateActions.Count;

    /// <summary>自检用：表演的第一段（没有被表演素材时为 null）。</summary>
    public string? FirstCelebrateAction => _celebrateActions.Count > 0 ? _celebrateActions[0] : null;

    /// <summary>自检用：表演的第二段（顺序断言用）。</summary>
    public string? SecondCelebrateAction => _celebrateActions.Count > 1 ? _celebrateActions[1] : null;

    /// <summary>自检用：还要接着演的段落数（0 = 没有正在进行的表演）。</summary>
    public int CelebrationQueueCount => _celebrateQueue.Count;

    /// <summary>自检用：走真实的 <see cref="PlayCelebration"/> 演一段。</summary>
    public bool PlayCelebrationForProbe() => PlayCelebration();

    /// <summary>自检用：把表演的队列清掉（模拟"收起窗口"这类打断）。</summary>
    public void CancelCelebrationForProbe() => CancelCelebration();

    /// <summary>自检用：此刻在播哪个动作。</summary>
    public string? CurrentAnimation => _player?.CurrentAction;

    /// <summary>自检用：此刻在播的是不是透明视频。</summary>
    public bool IsVideoPlaying => _player is { CurrentIsVideo: true };

    /// <summary>自检用：此刻有没有东西在播（含待机循环）。</summary>
    public bool IsAnimationPlaying => _player is { IsPlaying: true };

    /// <summary>自检用：ffmpeg 找没找到、在哪。</summary>
    public string FfmpegState => FfmpegLocator.Describe();

    /// <summary>自检用：按角色播一个动作（走真实的 <see cref="PlayRandomOfRole"/> 路径）。</summary>
    public bool PlayRoleOnce(string role)
    {
        IReadOnlyList<string> list;

        if (role == AnimActions.Idle) list = _idleActions;
        else if (role == AnimActions.Click) list = _clickActions;
        else if (role == AnimActions.Ambient) list = _ambientActions;
        else if (role == AnimActions.Drag) list = _dragActions;
        // 眨眼是原版那套素材的角色名：它有专门的 blink 动作，没有就退到点击
        else if (role == AnimActions.Blink) list = _clickActions;
        else list = Array.Empty<string>();

        return PlayRandomOfRole(list);
    }

    /// <summary>
    /// 自检用：视频动作是"边播边解"的，帧不会凭空出现，得给它一点真实时间。
    /// 这里一边推时间轴一边让出线程，直到解出 <paramref name="minFrames"/> 帧或超时。
    /// 返回实际解出了几帧。
    /// </summary>
    public int PumpVideoFrames(int minFrames, int timeoutMs = 4000)
    {
        var player = _player;
        if (player == null) return 0;

        var deadline = Environment.TickCount64 + timeoutMs;
        while (player.VideoFramesShown < minFrames && Environment.TickCount64 < deadline)
        {
            player.AdvanceBy(TimeSpan.FromMilliseconds(50));
            Thread.Sleep(20);
        }

        return player.VideoFramesShown;
    }

    /// <summary>
    /// 自检用：一直推时间轴直到当前动作自己结束，返回等了多久（毫秒）。
    ///
    /// "非循环动作播完会不会自己收手"必须验：收不了手就会一直卡在"播放中"，
    /// 后面所有动作都被堵住 —— 用户看到的是"点了一下之后就不动了"。
    /// </summary>
    public int PumpUntilStopped(int timeoutMs = 15000)
    {
        var player = _player;
        if (player == null) return 0;

        long start = Environment.TickCount64;
        long deadline = start + timeoutMs;

        while (player.IsPlaying && Environment.TickCount64 < deadline)
        {
            player.AdvanceBy(TimeSpan.FromMilliseconds(50));
            Thread.Sleep(15);
        }

        return (int)(Environment.TickCount64 - start);
    }

    /// <summary>
    /// 自检用：走一遍**真实的**鼠标移入 / 移出处理函数，而不是绕到 <see cref="SetHovering"/>。
    ///
    /// 移出那条路上有一个"要不要自动收起"的提前 return，形象还原必须排在它前面 ——
    /// 这个顺序错了只有真的把处理函数跑一遍才看得出来（探针不能真去移动用户的光标，
    /// 所以直接调用；两个处理函数都不读事件参数）。
    /// </summary>
    public void SimulateMouseEnter() => OnMouseEnter(this, null!);

    /// <inheritdoc cref="SimulateMouseEnter"/>
    public void SimulateMouseLeave() => OnMouseLeave(this, null!);

    /// <summary>自检用：配上了悬浮形象（差分图）没有。</summary>
    public bool HasHoverImage => _hoverImage != null;

    /// <summary>
    /// 自检用：此刻贴在鲸鱼上的就是悬浮形象那张吗。
    ///
    /// 比的是引用而不是像素 —— 悬浮切换本来就是"整张换掉"，
    /// 换成同一张图不会有任何视觉变化，所以引用相等正是这里要断言的东西。
    /// </summary>
    public bool ShowingHoverImage
        => _hoverImage != null && ReferenceEquals(WhaleImage.Source, _hoverImage);

    /// <summary>自检用：此刻贴在鲸鱼上的是不是静态本体那张。</summary>
    public bool ShowingStillImage
        => _stillImage != null && ReferenceEquals(WhaleImage.Source, _stillImage);

    /// <summary>
    /// 自检用：此刻贴在鲸鱼上的是不是动画的某一帧。
    ///
    /// 两种素材的"帧"是两种类型：精灵图切出来的是 <see cref="CroppedBitmap"/>，
    /// 透明视频解出来的是 <see cref="WriteableBitmap"/>（复用同一张，所以不能靠引用比较）。
    /// </summary>
    public bool ShowingAnimationFrame
        => WhaleImage.Source is CroppedBitmap || WhaleImage.Source is WriteableBitmap;

    /// <summary>自检用：动画播完会落回的那张图（悬浮时为悬浮形象，否则为本体）。</summary>
    public BitmapSource? AnimBaseImage => _player?.BaseImage;

    /// <summary>自检用：静态本体的像素尺寸。和悬浮形象不一致就会在切换时跳一下。</summary>
    public (int Width, int Height)? StillImageSize => SizeOf(_stillImage);

    /// <summary>自检用：悬浮形象的像素尺寸。</summary>
    public (int Width, int Height)? HoverImageSize => SizeOf(_hoverImage);

    private static (int Width, int Height)? SizeOf(BitmapSource? image)
        => image == null ? null : (image.PixelWidth, image.PixelHeight);

    // ------------------------------------------------------------------
    // 状态显示
    // ------------------------------------------------------------------

    private void RefreshStatus()
    {
        try
        {
            var today = DateTime.Today;
            var day = App.Store.Book.FindDay(today);
            int entries = day?.Entries.Count ?? 0;
            int open = day?.OpenCount ?? 0;
            int files = App.Store.FilesOnDate(today).Count();

            TodayText.Text = entries == 0 && files == 0
                ? "今天还没有记录"
                : $"{entries} 条记录 · {files} 个文件";

            CountdownText.Text = App.Settings.Settings.ReminderEnabled
                ? App.Reminder.RemainingText
                : "提醒已关闭";

            // 状态条右上角那两颗角标（见 FloatingWindow.xaml 的 BadgeGroup）。
            // 记录数跟 entries 走、未收尾跟 open 走，两个数各管各的。
            //
            // 收起态的小胶囊没有状态条，所以它身上**还留着一颗**（CollapsedBadge）。
            // 那颗必须和展开态的 OpenBadge 同数字同显隐 ——
            // 不然同一个"未收尾"收起／展开会看到两个数，那是纯粹的显示 bug。
            TodayBadge.Visibility = entries > 0 ? Visibility.Visible : Visibility.Collapsed;
            TodayBadgeText.Text = entries.ToString();
            TodayBadge.ToolTip = entries > 0 ? $"今天已归档 {entries} 条记录" : null;

            OpenBadge.Visibility = open > 0 ? Visibility.Visible : Visibility.Collapsed;
            OpenBadgeText.Text = open.ToString();
            OpenBadge.ToolTip = open > 0 ? $"还有 {open} 条没收尾" : null;

            CollapsedBadge.Visibility = open > 0 ? Visibility.Visible : Visibility.Collapsed;
            CollapsedBadgeText.Text = open.ToString();
            CollapsedBadge.ToolTip = open > 0 ? $"还有 {open} 条没收尾" : null;
        }
        catch
        {
            // 悬浮窗刷新失败不影响主流程
        }
    }

    // ------------------------------------------------------------------
    // 自检：状态条右上角的两颗角标
    // ------------------------------------------------------------------

    /// <summary>自检用：窗口坐标系里的一个矩形（相对本窗口客户区左上角，设备无关像素）。</summary>
    public readonly record struct ProbeRect(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Right - Left;
        public double Height => Bottom - Top;

        public override string ToString()
            => $"({Left:0.#},{Top:0.#})-({Right:0.#},{Bottom:0.#}) 宽{Width:0.#}×高{Height:0.#}";
    }

    /// <summary>自检用：按当前数据重刷一遍角标，省得为了看角标去等一次真实刷新。</summary>
    public void RefreshStatusForProbe() => RefreshStatus();

    /// <summary>自检用：状态条第一行的文案。</summary>
    public string TodayTextForProbe => TodayText.Text;

    /// <summary>自检用：记录数角标的文案。</summary>
    public string TodayBadgeLabelForProbe => TodayBadgeText.Text;

    /// <summary>自检用：记录数角标是否在显示。</summary>
    public bool TodayBadgeVisibleForProbe => TodayBadge.Visibility == Visibility.Visible;

    /// <summary>自检用：未收尾角标的文案。</summary>
    public string OpenBadgeLabelForProbe => OpenBadgeText.Text;

    /// <summary>自检用：未收尾角标是否在显示。</summary>
    public bool OpenBadgeVisibleForProbe => OpenBadge.Visibility == Visibility.Visible;

    /// <summary>自检用：收起态胶囊上那颗角标的文案（必须和展开态那颗一致）。</summary>
    public string CollapsedBadgeLabelForProbe => CollapsedBadgeText.Text;

    /// <summary>自检用：收起态胶囊上那颗角标是否在显示。</summary>
    public bool CollapsedBadgeVisibleForProbe => CollapsedBadge.Visibility == Visibility.Visible;

    /// <summary>
    /// 自检用：状态条第一行文字**一个省略号都不打**时需要的宽度。
    ///
    /// 用它来验"角标没把文字挤没"：文字块在星号列里是拉伸的，
    /// 光看它的 ActualWidth 永远等于列宽，看不出内容被 TextTrimming 截了。
    /// </summary>
    public double TodayTextNaturalWidthForProbe
    {
        get
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(TodayText.FontFamily, TodayText.FontStyle,
                TodayText.FontWeight, TodayText.FontStretch);
            var formatted = new FormattedText(
                TodayText.Text ?? string.Empty,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                TodayText.FontSize,
                Brushes.Black,
                dpi);
            return formatted.WidthIncludingTrailingWhitespace;
        }
    }

    /// <summary>自检用：状态条 / 文字 / 角标组各自的矩形。窗口还没量过布局时为 null。</summary>
    public (ProbeRect Bar, ProbeRect Texts, ProbeRect? Badges)? BadgeGeometryForProbe
    {
        get
        {
            try
            {
                var bar = RectOf(StatusBar);
                var texts = RectOf(TodayText);
                if (bar == null || texts == null) return null;
                var badges = BadgeGroup.Visibility == Visibility.Visible ? RectOf(BadgeGroup) : null;
                return (bar.Value, texts.Value, badges);
            }
            catch
            {
                return null;
            }
        }
    }

    private ProbeRect? RectOf(FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;
        var origin = element.TransformToAncestor(RootGrid).Transform(new Point(0, 0));
        return new ProbeRect(origin.X, origin.Y,
            origin.X + element.ActualWidth, origin.Y + element.ActualHeight);
    }

    // ------------------------------------------------------------------
    // 提醒气泡
    // ------------------------------------------------------------------

    public void ShowBubble(string title, string message, int urgency = 0)
    {
        try
        {
            if (!IsVisible) Show();

            Expand();

            BubbleTitle.Text = title;
            BubbleBody.Text = message;
            BubbleBody.Visibility = string.IsNullOrWhiteSpace(message)
                ? Visibility.Collapsed
                : Visibility.Visible;

            double holdMs = urgency >= 2 ? 11000 : 6500;
            var anim = new DoubleAnimationUsingKeyFrames();
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170 + holdMs))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170 + holdMs + 450))));
            Bubble.BeginAnimation(OpacityProperty, anim);

            _hasBubble = true;
            var hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170 + holdMs + 500) };
            hide.Tick += (_, _) =>
            {
                hide.Stop();
                _hasBubble = false;
                if (!IsMouseOver && App.Settings.Settings.FloatingAutoCollapse) Collapse();
            };
            hide.Start();

            // 有专门画的提醒动作就用它（画师可以做成"举牌子""摇铃"之类）；
            // 没有就沿用原来的"整体上下弹一下" —— 提醒一定要动起来，这是三种渠道之外
            // 唯一能拽住注意力的东西，降级但绝不能不响。
            if (urgency >= 1 && !PlayAnimation(AnimActions.Alert))
                Bounce(urgency >= 2 ? 16 : 9);
        }
        catch
        {
            // 气泡失败不影响托盘通知和音效
        }
    }

    /// <summary>鲸鱼娘上下弹一下，比纯文字更能拽住注意力。</summary>
    private void Bounce(double amplitude)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(900),
        };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(-amplitude, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170)))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)))
        { EasingFunction = new BounceEase { Bounces = 2, Bounciness = 2 } });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(-amplitude * 0.6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(560)))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(760)))
        { EasingFunction = new BounceEase { Bounces = 2, Bounciness = 2 } });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));

        var transform = new TranslateTransform();
        WhaleImage.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.YProperty, anim);
    }
}
