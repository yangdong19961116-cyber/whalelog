using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WhaleLog.Views;

/// <summary>
/// 托盘右键菜单。
///
/// 刻意不用 ContextMenu / MenuItem —— 它们要改模板才能跟主题走，
/// 而这里只是一个扁平列表，用一个无边框小窗口实现更简单、样式也完全可控。
///
/// 后来多了一件事：**二级页**（自选播放动画）。它不是另开一个窗口，
/// 而是在本窗口里换一屏内容 —— 本窗口失焦即关，真去弹子窗口的话，
/// 子窗口拿到焦点的瞬间就会把父菜单关掉（连子窗口自己一起没）。
/// </summary>
public sealed partial class TrayMenuWindow : Window
{
    /// <summary>一行文字的最大宽度。动作名再长也不让整个菜单横向长出去。</summary>
    private const double RowTextMaxWidth = 300;

    private bool _closing;

    public TrayMenuWindow()
    {
        InitializeComponent();
    }

    /// <summary>往菜单里加一项。checked 为 true 时前面显示对勾。</summary>
    public void AddItem(string label, Action action, bool checkedState = false, bool isLast = false)
    {
        if (isLast) AddSeparator();

        AddRow(checkedState ? "✓  " + label : "    " + label, action, closeFirst: true);
    }

    /// <summary>
    /// 加一项**点了不关菜单**的导航项：进二级页（<paramref name="isBack"/> = false），
    /// 或者从二级页返回（= true）。末尾那个 ▸ 是给用户的唯一提示：这项后面还有东西。
    /// </summary>
    public void AddNavItem(string label, Action onClick, bool isBack = false)
        => AddRow(isBack ? "←  " + label : "    " + label + "  ▸", onClick, closeFirst: false);

    /// <summary>二级页里的分组标题（灰色、不可点）。</summary>
    public void AddHeader(string text) => ItemsHost.Children.Add(MakeHeader(text));

    /// <summary>
    /// 开一块能**反复重填**的区域（筛选列表用它）。
    ///
    /// 列表要跟着关键词重列，但绝不能连搜索框一起重建 ——
    /// 中文输入法正组着词的时候把 TextBox 换掉，组合串会当场丢掉。
    /// </summary>
    public Section AddSection()
    {
        var host = new StackPanel();
        ItemsHost.Children.Add(host);
        return new Section(this, host);
    }

    /// <summary>
    /// 加一个筛选框，返回控件本身（调用方可以 Focus 它）。
    ///
    /// 每敲一个字就回调一次：列表只有一百来项，重建一遍比做防抖简单，
    /// 也不会出现"输入完了结果还是旧的"那种中间态。
    /// </summary>
    public TextBox AddSearchBox(Action<string> onChanged)
    {
        var box = new TextBox
        {
            FontSize = 12.5,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(2, 2, 2, 4),
            MinWidth = 176,
            ToolTip = "输入关键词筛选动作，比如「吃」「舞」「工作」",
        };
        box.SetResourceReference(Control.BackgroundProperty, "InputBackgroundBrush");
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        box.SetResourceReference(Control.BorderBrushProperty, "BorderSubtleBrush");
        box.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimaryBrush");
        box.SetResourceReference(TextBox.SelectionBrushProperty, "SelectionBrush");

        // 空的时候得有一句话说明这个框是干什么的 ——
        // 光摆一个空输入框，用户不会想到"这儿能打字在一百多个动作里筛"。
        // WPF 的 TextBox 没有占位文字，就在同一格里叠一层灰字，并让它不吃鼠标。
        var hint = new TextBlock
        {
            Text = "搜动作：吃 / 舞 / 工作…",
            FontSize = 12.5,
            Margin = new Thickness(11, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");

        var layer = new Grid();
        layer.Children.Add(box);
        layer.Children.Add(hint);

        void SyncHint() => hint.Visibility = box.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        box.TextChanged += (_, _) =>
        {
            SyncHint();
            onChanged(box.Text);
        };
        SyncHint();

        ItemsHost.Children.Add(layer);
        return box;
    }

    /// <summary>清空当前页的内容（换页时用）。</summary>
    public void ClearItems() => ItemsHost.Children.Clear();

    /// <summary>
    /// 换页之后重新贴回工作区。
    ///
    /// 二级页比一级高得多（一百多项），而窗口的 Left/Top 是不动的 ——
    /// 不重新收一次的话，菜单会从屏幕下沿直接长出去，末尾那些动作根本点不到。
    /// </summary>
    public void Relayout()
    {
        // 没显示过的窗口没有 ActualWidth，也谈不上越界 ——
        // 自检要在不弹窗的前提下把菜单搭出来看内容，必须能安全走过这里。
        if (!IsVisible) return;

        UpdateLayout();
        ClampToWorkArea();
    }

    /// <summary>
    /// 自检用：当前页每一行的文字（分组标题、按钮、搜索框各算一行）。
    ///
    /// 这个窗口的做法是**代码构造**而不是数据绑定，所以"菜单里到底有什么"
    /// 没有别的东西可查 —— 自检只能把构建结果念出来核对。
    /// </summary>
    public IReadOnlyList<string> RowTextsForProbe => Flatten(ItemsHost).ToList();

    /// <summary>
    /// 把一页拍平成一行行文字。
    ///
    /// **必须递归**：二级页的动作列表装在一块可重填的子面板里（见 <see cref="AddSection"/>），
    /// 只看最外层的话，那一百多个动作一个都读不到 —— 探针会以为"菜单是空的"，
    /// 而它恰恰是唯一能发现这件事的东西。
    /// </summary>
    private static IEnumerable<string> Flatten(Panel host)
    {
        foreach (var child in host.Children.OfType<FrameworkElement>())
        {
            switch (child)
            {
                case Button { Content: TextBlock tb }:
                    yield return tb.Text;
                    break;

                case TextBlock t:
                    yield return t.Text;
                    break;

                case TextBox:
                    yield return SearchBoxMarker;
                    break;

                case Panel nested:
                    foreach (var text in Flatten(nested)) yield return text;
                    break;

                default:
                    yield return SeparatorMarker;
                    break;
            }
        }
    }

    /// <summary>自检里表示"这里是个搜索框"的占位文字。</summary>
    public const string SearchBoxMarker = "<搜索框>";

    /// <summary>自检里表示"这里是条分隔线"的占位文字。</summary>
    public const string SeparatorMarker = "<分隔线>";

    /// <summary>在指定屏幕坐标弹出，自动规避屏幕边缘。</summary>
    public void ShowAt(int screenX, int screenY)
    {
        // 先测量一次拿到真实尺寸
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = screenX;
        Top = screenY;
        Show();
        UpdateLayout();

        ClampToWorkArea();

        Activate();
        Focus();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        RequestClose();
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) RequestClose();
        base.OnKeyDown(e);
    }

    /// <summary>把窗口收进工作区（右下越界就往上、往左贴）。</summary>
    private void ClampToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : 200;
        double h = ActualHeight > 0 ? ActualHeight : 200;

        if (Left + w > work.Right) Left = Math.Max(work.Left, work.Right - w);
        if (Top + h > work.Bottom) Top = Math.Max(work.Top, work.Bottom - h);
    }

    private void AddSeparator()
    {
        ItemsHost.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(6, 4, 6, 4),
            Background = (Brush)(TryFindResource("BorderSubtleBrush") ?? Brushes.Gray),
        });
    }

    private void AddRow(string text, Action onClick, bool closeFirst)
        => ItemsHost.Children.Add(MakeRow(text, onClick, closeFirst));

    private TextBlock MakeHeader(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            Margin = new Thickness(12, 9, 10, 2),
            MaxWidth = RowTextMaxWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        return tb;
    }

    /// <summary>
    /// 造一行。<paramref name="closeFirst"/> 决定点完要不要先关菜单：
    /// 普通项必须关（否则它弹的对话框会被菜单盖住），
    /// 而二级页的导航项**不能**关 —— 关了就没地方去了。
    /// </summary>
    private Button MakeRow(string text, Action onClick, bool closeFirst)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                MaxWidth = RowTextMaxWidth,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(10, 7, 10, 7),
        };

        btn.SetResourceReference(StyleProperty, "GhostButton");
        btn.Click += (_, _) =>
        {
            if (closeFirst) RequestClose();
            // 让菜单先关掉（或先换完页）再执行动作
            Dispatcher.BeginInvoke(new Action(onClick), System.Windows.Threading.DispatcherPriority.Background);
        };

        return btn;
    }

    /// <summary>
    /// 统一关闭入口。失焦会自动关菜单，而关闭过程本身又会让窗口失焦 ——
    /// 不设守卫就会重入 Close() 并抛"在窗口关闭期间无法调用 Close"。
    /// </summary>
    private void RequestClose()
    {
        if (_closing) return;
        _closing = true;
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // 已经进入关闭流程，忽略
        }
    }

    /// <summary>
    /// 菜单里一块可以反复重填的区域（见 <see cref="AddSection"/>）。
    /// 只放标题和行 —— 它随时会被清空重来，所以不放分隔线、不放搜索框。
    /// </summary>
    public sealed class Section
    {
        private readonly TrayMenuWindow _owner;
        private readonly StackPanel _host;

        internal Section(TrayMenuWindow owner, StackPanel host)
        {
            _owner = owner;
            _host = host;
        }

        public void Clear() => _host.Children.Clear();

        public void AddHeader(string text) => _host.Children.Add(_owner.MakeHeader(text));

        /// <summary>加一行可点的动作：点了先关菜单再执行。</summary>
        public void AddItem(string label, Action onClick)
            => _host.Children.Add(_owner.MakeRow("    " + label, onClick, closeFirst: true));
    }
}
