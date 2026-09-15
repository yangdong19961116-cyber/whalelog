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

    private const double ExpandedWidth = 236;
    private const double ExpandedHeight = 296;
    private const double CollapsedSize = 60;
    private const double SnapThreshold = 40;

    /// <summary>静态形象时鲸鱼图的高度（设计像素）。XAML 里 WhaleImage 的 Height 也是这个数。</summary>
    private const double StaticWhaleHeight = AnimLibrary.StaticWhaleBoxHeight;

    /// <summary>
    /// 鲸鱼框高度的允许区间。
    ///
    /// 正常情况下它由帧表算出来（样例是 198），这里只是兜住手写坏的帧表：
    /// 太小看不清，太大能把窗口撑成一块占半个屏幕的空白。
    /// </summary>
    private const double MinWhaleHeight = 120;
    private const double MaxWhaleHeight = 280;

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

    private static readonly Random BlinkRng = new();

    /// <summary>
    /// 当前鲸鱼图的高度（设计像素）。动态素材的画布比静态的大，
    /// 这个值要跟着变大，否则角色会凭空缩小 —— 见 AnimSheet.DisplayBoxHeight。
    /// </summary>
    private double _whaleHeight = StaticWhaleHeight;

    /// <summary>此刻是否启用了动态素材。</summary>
    private bool _animationsOn;

    /// <summary>上次按哪个"动画开关"值建的播放器，用来判断设置改动后要不要重建。</summary>
    private bool _animationsSettingApplied;
    private bool _animationsSettingKnown;

    /// <summary>
    /// 展开态的设计高度。
    ///
    /// 鲸鱼框高一点，窗口就高一点 —— 两者必须出自同一个量。只把图放大而不动窗口，
    /// 多出来的部分会顶掉气泡的位置；只把窗口放大而不动图，鲸鱼下方会多一条空白。
    /// </summary>
    private double ExpandedDesignHeight => ExpandedHeight + (_whaleHeight - StaticWhaleHeight);

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

        // 「小鲸鱼动画」开关翻动了要重建播放器（否则关掉之后还在眨）
        if (!_animationsSettingKnown || _animationsSettingApplied != s.FloatingAnimations)
            ReloadAnimations();

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
        var whale = IconAssets.Load("whale");
        if (whale != null) WhaleImage.Source = whale;

        var head = IconAssets.Load("whale-head");
        if (head != null) HeadImage.Source = head;

        // 动态素材跟着一起重扫：用户刚把 sheet.png / sheet.json 拷进来，就该立刻能播
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

        double designW = collapsed ? CollapsedSize : ExpandedWidth;
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
        if (!App.Settings.Settings.FloatingSnapToEdge)
        {
            _edge = Edge.None;
            return;
        }

        var a = CurrentWorkArea();
        var edge = NearestEdge();

        // 没进吸附范围就按离哪条边最近来决定锚定边，让后续收起有明确方向
        if (edge == Edge.None)
        {
            double dLeft = Left - a.Left;
            double dRight = a.Right - (Left + Width);
            double dTop = Top - a.Top;
            double dBottom = a.Bottom - (Top + Height);
            double min = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));
            edge = min == dLeft ? Edge.Left
                 : min == dRight ? Edge.Right
                 : min == dTop ? Edge.Top
                 : Edge.Bottom;
        }

        switch (edge)
        {
            case Edge.Left: Left = a.Left; break;
            case Edge.Right: Left = a.Right - Width; break;
            case Edge.Top: Top = a.Top; break;
            case Edge.Bottom: Top = a.Bottom - Height; break;
        }

        _edge = edge;
        ClampToArea();
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
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!App.Settings.Settings.FloatingAutoCollapse) return;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    public void Collapse()
    {
        if (_collapsed || !_ready) return;

        _collapsed = true;
        ExpandedRoot.Visibility = Visibility.Collapsed;
        CollapsedRoot.Visibility = Visibility.Visible;

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
        try
        {
            // 阻塞式：返回时鼠标已经松开。
            //
            // 注意 WM_LBUTTONUP 是在这个模态移动循环里派发的 —— WPF 会在循环内部
            // 就抛出 MouseLeftButtonUp，**先于**这里后面的代码执行。所以判定不能在
            // 后面算完再等 Up 来读，必须在 OnLeftButtonUp 里独立算一遍。
            DragMove();
        }
        catch
        {
            // 鼠标状态异常（比如拖拽中按了 Esc）会抛，忽略即可
        }

        _dragging = false;

        if (HasMovedSincePress())
        {
            SnapToEdge();
            SavePosition();
            if (App.Settings.Settings.FloatingAutoCollapse) Collapse();
        }
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
        // 素材没装或不支持时 PlayAnimation 返回 false，行为完全等于以前。
        PlayAnimation(AnimActions.Click);

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

        try
        {
            _blinkTimer?.Stop();
            DetachPlayer();

            _anim = AnimLibrary.Scan();

            // 静态图先取出来：无论开不开动画，它都是"没在播"时的底图。
            // anim\base.png 也在素材清单里，取到了它就是这张。
            var still = IconAssets.Load("whale") as BitmapSource;
            if (still != null) WhaleImage.Source = still;

            _animationsOn = wanted && _anim.HasAny;

            if (!_animationsOn)
            {
                _whaleHeight = StaticWhaleHeight;
            }
            else
            {
                // 画布从 610 变成 720 之后，框还留 168 的话角色会凭空小 15%。
                // 帧表没写 characterHeight 时算不出来，就老实保持静态尺寸。
                var box = _anim.DisplayBoxHeight(AnimLibrary.FloatingCharacterDesignHeight);
                _whaleHeight = box.HasValue
                    ? Math.Clamp(box.Value, MinWhaleHeight, MaxWhaleHeight)
                    : StaticWhaleHeight;

                _player = new SpritePlayer(WhaleImage) { BaseImage = still };

                foreach (var action in _anim.ActionNames)
                {
                    var sheet = _anim.Find(action);
                    var path = _anim.SheetImagePath(action);
                    if (sheet != null && path != null) _player.Register(action, sheet, path);
                }

                _player.Finished += OnAnimationFinished;
            }

            WhaleImage.Height = _whaleHeight;

            // 只有位置已经摆过才能重排尺寸 —— 首次 Loaded 时 Left/Top 还是 NaN，
            // 这时候走 ResizeAnchored 会把窗口摆到 NaN 上去。
            if (_ready && _positioned) ApplySize();

            ScheduleBlink();
        }
        catch (Exception ex)
        {
            CrashLog.Note($"[动画] 初始化失败，退回静态形象：{ex}");

            _animationsOn = false;
            _whaleHeight = StaticWhaleHeight;
            WhaleImage.Height = _whaleHeight;
            DetachPlayer();
        }
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
    }

    /// <summary>此刻适不适合眨眼：开着动画、展开着、显示着、鼠标不在上面。</summary>
    private bool CanBlinkNow
        => _animationsOn && !_collapsed && IsVisible && !IsMouseOver && !_dragging;

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
                ? $"已启用：鲸鱼框 {_whaleHeight:0.#}px，窗口设计高 {ExpandedDesignHeight:0.#}px，" +
                  $"登记动作 {_player?.RegisteredCount ?? 0} 个"
                : $"未启用（开关={App.Settings.Settings.FloatingAnimations}，" +
                  $"扫到动作 {_anim.ActionNames.Count} 个）";

            return head + Environment.NewLine + _anim.Describe();
        }
    }

    /// <summary>自检用：直接拿到播放器验证真实的播放/推进逻辑。</summary>
    public SpritePlayer? AnimPlayer => _player;

    /// <summary>自检用：此刻是否启用了动态素材。</summary>
    public bool AnimationsOn => _animationsOn;

    /// <summary>自检用：鲸鱼框的当前高度（设计像素）。</summary>
    public double WhaleHeight => _whaleHeight;

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

            CountBadge.Visibility = open > 0 ? Visibility.Visible : Visibility.Collapsed;
            CountBadgeText.Text = open.ToString();
            CollapsedBadge.Visibility = open > 0 ? Visibility.Visible : Visibility.Collapsed;
            CollapsedBadgeText.Text = open.ToString();
        }
        catch
        {
            // 悬浮窗刷新失败不影响主流程
        }
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
