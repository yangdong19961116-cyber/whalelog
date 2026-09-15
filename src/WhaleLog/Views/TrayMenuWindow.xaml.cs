using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WhaleLog.Views;

/// <summary>
/// 托盘右键菜单。
///
/// 刻意不用 ContextMenu / MenuItem —— 它们要改模板才能跟主题走，
/// 而这里只有一个不含子菜单的扁平列表，用一个无边框小窗口实现更简单、
/// 样式也完全可控。
/// </summary>
public sealed partial class TrayMenuWindow : Window
{
    private bool _closing;

    public TrayMenuWindow()
    {
        InitializeComponent();
    }

    /// <summary>往菜单里加一项。checked 为 true 时前面显示对勾。</summary>
    public void AddItem(string label, Action action, bool checkedState = false, bool isLast = false)
    {
        if (isLast)
        {
            ItemsHost.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(6, 4, 6, 4),
                Background = (Brush)(TryFindResource("BorderSubtleBrush") ?? Brushes.Gray),
            });
        }

        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = checkedState ? "✓  " + label : "    " + label,
                FontSize = 12.5,
            },
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(10, 7, 10, 7),
        };

        btn.SetResourceReference(StyleProperty, "GhostButton");
        btn.Click += (_, _) =>
        {
            RequestClose();
            // 让菜单先关掉再执行动作，避免动作里的对话框被菜单挡住
            Dispatcher.BeginInvoke(new Action(action), System.Windows.Threading.DispatcherPriority.Background);
        };

        ItemsHost.Children.Add(btn);
    }

    /// <summary>在指定屏幕坐标弹出，自动规避屏幕边缘。</summary>
    public void ShowAt(int screenX, int screenY)
    {
        // 先测量一次拿到真实尺寸
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = screenX;
        Top = screenY;
        Show();
        UpdateLayout();

        var work = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : 200;
        double h = ActualHeight > 0 ? ActualHeight : 200;

        if (Left + w > work.Right) Left = Math.Max(work.Left, work.Right - w);
        if (Top + h > work.Bottom) Top = Math.Max(work.Top, work.Bottom - h);

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
}
