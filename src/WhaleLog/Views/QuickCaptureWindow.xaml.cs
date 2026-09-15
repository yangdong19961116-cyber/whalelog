using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WhaleLog.Models;
using WhaleLog.Services;

namespace WhaleLog.Views;

/// <summary>
/// 快速记录窗：按全局快捷键弹出的迷你输入框。
///
/// 存在的理由是把"记一笔"的操作路径压到最短 —— 不用先找到小鲸鱼、点开主面板、
/// 再定位到输入框。呼出即写，Enter 就存。
///
/// 失焦自动关闭，所以它永远不会挡在桌面上碍事。
/// </summary>
public sealed partial class QuickCaptureWindow : Window
{
    private readonly List<ToggleButton> _chips = new();
    private string _selectedCategory = NoteCategories.Work;
    private bool _saving;
    private bool _closing;

    public QuickCaptureWindow()
    {
        InitializeComponent();
        BuildCategoryChips();

        DateHint.Text = $"· {DateTime.Today:MM月dd日} {ReportService.WeekdayText(DateTime.Today)}";

        Loaded += OnLoaded;
    }

    /// <summary>保存成功后触发，参数是刚记下的那条内容（供悬浮窗弹个确认气泡）。</summary>
    public event Action<NoteEntry>? Captured;

    private void BuildCategoryChips()
    {
        foreach (var category in NoteCategories.All)
        {
            var chip = new ToggleButton
            {
                Content = category,
                IsChecked = category == _selectedCategory,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(10, 4, 10, 4),
            };
            chip.SetResourceReference(StyleProperty, "ChipToggle");
            chip.Click += (s, _) =>
            {
                if (s is not ToggleButton clicked) return;
                _selectedCategory = clicked.Content as string ?? NoteCategories.Work;
                foreach (var c in _chips) c.IsChecked = ReferenceEquals(c, clicked);
            };
            CategoryChips.Children.Add(chip);
            _chips.Add(chip);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionNearCursor();

        // 全局热键呼出的窗口不一定马上拿到键盘焦点，
        // 排到 Input 优先级再抢一次，输入框才稳。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Activate();
            Input.Focus();
            Keyboard.Focus(Input);
        }), DispatcherPriority.Input);
    }

    private void PositionNearCursor()
    {
        try
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double x = cursor.X / dpi.DpiScaleX;
            double y = cursor.Y / dpi.DpiScaleY;

            UpdateLayout();
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : 200;

            // 光标右下方一点，超出屏幕就翻到另一侧
            double left = x - width * 0.35;
            double top = y + 16;

            var work = SystemParameters.WorkArea;
            if (left < work.Left + 4) left = work.Left + 4;
            if (left + width > work.Right - 4) left = work.Right - width - 4;
            if (top + height > work.Bottom - 4) top = Math.Max(work.Top + 4, y - height - 16);

            Left = left;
            Top = top;
        }
        catch
        {
            // 定位失败就用默认位置，不影响输入
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                RequestClose();
                break;

            case Key.Enter:
                // Enter 保存，Shift+Enter 换行 —— 记一笔的场景里"敲完就发"比换行常用得多
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    e.Handled = true;
                    Save();
                }
                break;
        }
    }

    private void Save()
    {
        if (_saving) return;
        _saving = true;

        try
        {
            var raw = Input.Text.Trim();
            if (raw.Length == 0)
            {
                RequestClose();
                return;
            }

            var (text, tag) = SplitTag(raw);
            var day = App.Store.GetOrCreateDay(DateTime.Today);

            var entry = new NoteEntry
            {
                Text = text,
                Category = _selectedCategory,
                Tag = tag,
                CreatedAt = DateTime.Now,
            };

            day.Entries.Add(entry);
            App.Store.Save();

            Captured?.Invoke(entry);
        }
        catch
        {
            _saving = false;
            return;
        }

        RequestClose();
    }

    /// <summary>把开头的 `#标签` 拆出来，剩下的是正文。</summary>
    private static (string Text, string Tag) SplitTag(string raw)
    {
        if (!raw.StartsWith('#')) return (raw, "");

        int space = raw.IndexOfAny(new[] { ' ', '\t', '　' });
        if (space <= 1) return (raw, "");

        var tag = raw[1..space].Trim();
        var text = raw[(space + 1)..].Trim();
        return text.Length == 0 ? (raw, "") : (text, tag);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // 点别处就收起来，不留一个浮窗在桌面上
        if (_saving) return;
        RequestClose();
    }

    /// <summary>
    /// 统一的关闭入口。
    ///
    /// 必须带 _closing 守卫：Close() 的过程中窗口会失活，失活又回调到这里再 Close()，
    /// WPF 会抛"在窗口关闭期间无法调用 Close"。这个异常会被全局处理器吞掉，
    /// 表面上窗口正常关掉了，但错误日志里会一直堆栈，问题会被掩盖。
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

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ((App)Application.Current).NotifyQuickCaptureClosed(this);
    }
}
