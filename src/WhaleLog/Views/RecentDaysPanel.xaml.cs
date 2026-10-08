using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WhaleLog.Services;

namespace WhaleLog.Views;

/// <summary>
/// 往期记录面板：按日期列出"有记录的日子"，点一下跳过去。
///
/// 行的内容全部来自 <see cref="DayIndex"/> 的摘要（和一个文件列表），
/// 这里只负责把摘要摆成能点的一列 —— 判定不在这一层。
/// </summary>
public partial class RecentDaysPanel : UserControl
{
    /// <summary>清单里的一行。日期单独记着，跳转时不用再去解析控件上的文字。</summary>
    private sealed class Row
    {
        public string Date = "";
        public Button Button = null!;
    }

    private readonly List<Row> _rows = new();

    private List<DayDigest> _digests = new();
    private DateTime _today = DateTime.Today;
    private DateTime _selected = DateTime.Today;
    private int _cursor;

    public RecentDaysPanel()
    {
        InitializeComponent();
    }

    /// <summary>用户选中了某一天。</summary>
    public event Action<DateTime>? DateChosen;

    /// <summary>用户要求收起面板（按了 Esc）。面板自己关不掉弹出层，交给宿主处理。</summary>
    public event Action? DismissRequested;

    // ------------------------------------------------------------------
    // 给诊断自检看的
    // ------------------------------------------------------------------

    public int RowCount => _rows.Count;

    /// <summary>当前清单里每一行的日期（yyyy-MM-dd），按显示顺序。</summary>
    public IReadOnlyList<string> RowDates => _rows.Select(r => r.Date).ToList();

    /// <summary>当前清单里每一行右侧的条数文案，按显示顺序。</summary>
    public IReadOnlyList<string> RowCounts => _rows.Select(r => DayIndex.CountsText(DigestOf(r.Date))).ToList();

    /// <summary>键盘光标停在第几行（-1 表示清单是空的）。</summary>
    public int CursorIndex => _cursor;

    public string SummaryLine => SummaryText.Text;

    /// <summary>过滤框的内容。写进去就等于用户在打字（会立刻重排清单）。</summary>
    public string FilterText
    {
        get => FilterInput.Text;
        set => FilterInput.Text = value;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// 打开面板：灌入数据并定位到当前正在查看的那一天。
    /// days 传 <see cref="DayIndex.Build"/> 的结果（从新到旧）。
    /// </summary>
    public void Show(DateTime selected, IEnumerable<DayDigest>? days)
    {
        _today = DateTime.Today;
        _selected = selected.Date;
        _digests = (days ?? Enumerable.Empty<DayDigest>()).ToList();

        // 上次的过滤词不该留到下一次（和月历的「跳到」框一致）：
        // 留着的话，用户点开面板看到的是一个被悄悄筛过的清单，只会以为"记录丢了"。
        FilterInput.Clear();
        Rebuild();
    }

    /// <summary>把键盘焦点放进过滤框：打开面板就能直接敲字，也能用 ↑↓ / Esc。</summary>
    public void FocusFilterInput()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            FilterInput.Focus();
            Keyboard.Focus(FilterInput);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>选中第 index 行（诊断自检用它模拟点击）。</summary>
    public bool PickIndex(int index)
    {
        if (index < 0 || index >= _rows.Count) return false;
        if (!DateTime.TryParse(_rows[index].Date, out var date)) return false;

        DateChosen?.Invoke(date.Date);
        return true;
    }

    /// <summary>选中某个日期那一行（那天不在当前清单里就返回 false）。</summary>
    public bool PickDate(string dateKey)
        => PickIndex(_rows.FindIndex(r => r.Date == dateKey));

    /// <summary>挪一下键盘光标。已经在头/尾时返回 false（不假装动过了）。</summary>
    public bool TryMoveCursor(int delta)
    {
        if (_rows.Count == 0) return false;

        int next = Math.Clamp(_cursor + delta, 0, _rows.Count - 1);
        if (next == _cursor) return false;

        _cursor = next;
        ApplyCursor();
        _rows[_cursor].Button.BringIntoView();
        return true;
    }

    /// <summary>Enter 的行为：跳去光标所在的那天。</summary>
    public bool PickCursor() => PickIndex(_cursor);

    // ------------------------------------------------------------------

    private string SelectedKey => _selected.ToString("yyyy-MM-dd");

    private DayDigest DigestOf(string dateKey)
        => _digests.FirstOrDefault(d => d.Date == dateKey)
           ?? new DayDigest { Date = dateKey };

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e) => Rebuild();

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                DismissRequested?.Invoke();
                return;

            case Key.Up:
                e.Handled = true;   // 别让 ↑↓ 跑去滚外层的列表
                TryMoveCursor(-1);
                return;

            case Key.Down:
                e.Handled = true;
                TryMoveCursor(1);
                return;

            case Key.Enter:
                e.Handled = true;
                PickCursor();       // 空清单时 PickCursor 自然返回 false，不会炸
                return;
        }
    }

    private void Rebuild()
    {
        // 构造期间也会走到这里（InitializeComponent 之后 Clear/Show 前的第一次赋值），
        // 控件还没齐就什么都别做。
        if (RowsHost == null || FilterInput == null || SummaryText == null) return;

        RowsHost.Children.Clear();
        _rows.Clear();

        var keyword = FilterInput.Text.Trim();
        var shown = DayIndex.Search(_digests, keyword);

        string? month = null;
        foreach (var day in shown)
        {
            var label = DayIndex.MonthLabel(day.Date, _today);
            if (label != month)
            {
                month = label;
                var header = new TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    Margin = new Thickness(8, _rows.Count == 0 ? 2 : 10, 0, 3),
                };
                // 用 SetResourceReference 而不是 FindResource：切主题时能跟着换，
                // 而不用等下一次重建面板（面板开着的时候切主题是可能的）。
                header.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                RowsHost.Children.Add(header);
            }

            var button = BuildRow(day, keyword);
            _rows.Add(new Row { Date = day.Date, Button = button });
            RowsHost.Children.Add(button);
        }

        // 光标默认落在"当前正在看的这一天"上 —— 打开面板按 ↑ 就是上一个有记录的日子，
        // 这正是用户从这个面板想要的第一件事。那天不在筛出来的集合里就退回第一行。
        int index = _rows.FindIndex(r => r.Date == SelectedKey);
        _cursor = index >= 0 ? index : (_rows.Count > 0 ? 0 : -1);
        ApplyCursor();

        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = keyword.Length == 0
            ? "还没有可回溯的记录。"
            : $"没有包含「{keyword}」的日子。";

        SummaryText.Text = shown.Count == 0
            ? (keyword.Length == 0 ? "暂无" : "无匹配")
            : (keyword.Length == 0 ? DayIndex.SummaryText(shown) : $"命中 {shown.Count} 天");
    }

    private Button BuildRow(DayDigest day, string keyword)
    {
        var dateLabel = new TextBlock
        {
            Text = DayIndex.DateLabel(day.Date, _today),
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
        };
        // 正在看的那天用强调色：清单里一眼能认出"我在哪"
        dateLabel.SetResourceReference(TextBlock.ForegroundProperty,
            day.Date == SelectedKey ? "AccentBrush" : "TextPrimaryBrush");

        var weekday = new TextBlock
        {
            Text = DayIndex.WeekdayLabel(day.Date, _today),
            FontSize = 11.5,
            Margin = new Thickness(7, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            // 挤不下时先牺牲星期这两个字：它窄、而且是这一行里唯一能不要的信息。
            // 日期和右边的数字都得完整 —— 数字被裁是"看着正常、实际少一位"这种最坏的失败。
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        weekday.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");

        // 左半区两列：日期吃自己需要的宽度，剩下的全给星期（它自己会裁）
        var left = new Grid { VerticalAlignment = VerticalAlignment.Center };
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(dateLabel, 0);
        Grid.SetColumn(weekday, 1);
        left.Children.Add(dateLabel);
        left.Children.Add(weekday);

        var counts = new TextBlock { Text = DayIndex.CountsText(day), FontSize = 11.5 };
        counts.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(counts);

        // 没收尾的要显眼：这是"我还能回去补一下"的入口，不是纯统计
        if (day.HasOpen)
        {
            var open = new TextBlock
            {
                Text = $"· {day.Open} 未收尾",
                FontSize = 11.5,
                Margin = new Thickness(6, 0, 0, 0),
            };
            open.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            right.Children.Add(open);
        }

        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        line.Children.Add(left);
        line.Children.Add(right);

        var content = new StackPanel();
        content.Children.Add(line);

        // 只有真在搜的时候才占这一行：平时多一行字只是噪音
        if (keyword.Length > 0 && DayIndex.FirstMatch(day, keyword) is { } hit)
        {
            var snippet = new TextBlock
            {
                Text = "「" + keyword + "」" + Truncate(hit, 30),
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            snippet.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            content.Children.Add(snippet);
        }

        var button = new Button
        {
            Content = content,
            Style = (Style)FindResource("RecentRow"),
        };

        var captured = day.Date;
        button.Click += (_, _) => PickDate(captured);

        return button;
    }

    private void ApplyCursor()
    {
        var normal = (Style)FindResource("RecentRow");
        var active = (Style)FindResource("RecentRowActive");

        for (int i = 0; i < _rows.Count; i++)
            _rows[i].Button.Style = i == _cursor ? active : normal;
    }

    /// <summary>记录正文可能很长（也可能带换行），行里只给一小截。</summary>
    private static string Truncate(string text, int max)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
