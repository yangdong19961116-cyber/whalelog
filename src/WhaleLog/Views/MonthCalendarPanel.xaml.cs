using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WhaleLog.Services;

namespace WhaleLog.Views;

/// <summary>
/// 月历面板：整月日期一览，用来快速跳转和回溯。
///
/// 三个信息维度叠在一个格子里：
///   · 月份归属 —— 非本月的日子变淡，但仍可点（省掉"先翻月再选"的一步）
///   · 今天     —— 加一圈强调色描边
///   · 有记录   —— 数字下面点一个小圆点，翻回上周能一眼看出哪天写过东西
///
/// 固定 6 行 × 7 列共 42 格。有的月份 5 行就够，但保持 6 行可以让面板高度恒定，
/// 翻月时不会上下跳。
/// </summary>
public partial class MonthCalendarPanel : UserControl
{
    private const int Rows = 6;
    private const int Columns = 7;

    private static readonly string[] WeekdayLabels = { "一", "二", "三", "四", "五", "六", "日" };

    private DateTime _displayMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selected = DateTime.Today;
    private HashSet<string> _marked = new(StringComparer.Ordinal);

    public MonthCalendarPanel()
    {
        InitializeComponent();
        BuildWeekHeader();
    }

    /// <summary>用户选中了某一天（也可能是"今天"按钮）。</summary>
    public event Action<DateTime>? DateChosen;

    /// <summary>用户要求收起面板（按了 Esc）。面板自己关不掉弹出层，交给宿主处理。</summary>
    public event Action? DismissRequested;

    public DateTime DisplayMonth => _displayMonth;

    public DateTime SelectedDate => _selected;

    /// <summary>当前面板里按顺序排列的 42 个日期，第一个是网格左上角那天。</summary>
    public IReadOnlyList<DateTime> VisibleDates
        => Enumerable.Range(0, Rows * Columns).Select(i => GridStart().AddDays(i)).ToList();

    /// <summary>当前面板里被标记为"有记录"的格数。</summary>
    public int MarkedCount => VisibleDates.Count(d => _marked.Contains(DateKey(d)));

    /// <summary>是否还能往后翻月（不允许翻到未来的月份）。</summary>
    public bool CanGoNextMonth => NextMonthButton.IsEnabled;

    /// <summary>
    /// 打开面板并渲染指定月份。
    /// markedDates 传"有内容"的日期集合（yyyy-MM-dd），用来画小圆点。
    /// </summary>
    public void Show(DateTime displayMonth, DateTime selected, IEnumerable<string>? markedDates)
    {
        _displayMonth = new DateTime(displayMonth.Year, displayMonth.Month, 1);
        _selected = selected.Date;
        _marked = markedDates == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(markedDates, StringComparer.Ordinal);

        JumpInput.Clear();   // 上次输入不该留到下一次
        Rebuild();
    }

    /// <summary>把键盘焦点放进跳转框，打开面板就能直接敲日期。</summary>
    public void FocusJumpInput()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            JumpInput.Focus();
            Keyboard.Focus(JumpInput);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>翻月。delta 为 -1 / +1。</summary>
    public void GoMonth(int delta) => Show(_displayMonth.AddMonths(delta), _selected, _marked);

    /// <summary>
    /// 选中某一天：更新选中态并抛出 DateChosen。
    /// 日期格里点一下最终也走到这里，所以"点格子"和"程序切换"是同一条路径。
    /// </summary>
    public bool Pick(DateTime date)
    {
        var day = date.Date;
        if (day > DateTime.Today) return false;   // 未来的日期不给选

        _selected = day;
        if (day.Month != _displayMonth.Month || day.Year != _displayMonth.Year)
            _displayMonth = new DateTime(day.Year, day.Month, 1);

        Rebuild();
        DateChosen?.Invoke(day);
        return true;
    }

    // ------------------------------------------------------------------

    private void BuildWeekHeader()
    {
        WeekHeader.Children.Clear();
        foreach (var label in WeekdayLabels)
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2),
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            WeekHeader.Children.Add(text);
        }
    }

    /// <summary>网格左上角对应的日期（往回退到所在周的周一）。</summary>
    private DateTime GridStart()
    {
        int offset = ((int)_displayMonth.DayOfWeek + 6) % 7;   // 周一 = 0
        return _displayMonth.AddDays(-offset);
    }

    private void Rebuild()
    {
        MonthText.Text = $"{_displayMonth:yyyy 年 M 月}";

        // 已经是本月就不用再往前翻
        NextMonthButton.IsEnabled = _displayMonth < new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

        DayGrid.Children.Clear();

        var start = GridStart();
        int marked = 0;

        for (int i = 0; i < Rows * Columns; i++)
        {
            var date = start.AddDays(i);
            if (_marked.Contains(DateKey(date))) marked++;
            DayGrid.Children.Add(CreateDayCell(date));
        }

        StatsText.Text = marked == 0
            ? $"今天 {DateTime.Today:M/d}"
            : $"今天 {DateTime.Today:M/d} · {marked} 天有记录";

        RefreshJumpHint();
    }

    /// <summary>日期的字符串键，用来和「有记录」集合比对。</summary>
    private static string DateKey(DateTime date) => date.ToString("yyyy-MM-dd");

    private UIElement CreateDayCell(DateTime date)
    {
        bool inMonth = date.Month == _displayMonth.Month && date.Year == _displayMonth.Year;
        bool isToday = date.Date == DateTime.Today;
        bool isSelected = date.Date == _selected;
        bool isFuture = date.Date > DateTime.Today;
        bool isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        bool hasContent = _marked.Contains(DateKey(date));

        var cell = new Button
        {
            Width = 36,
            Height = 36,
            Margin = new Thickness(1),
            BorderThickness = new Thickness(isToday && !isSelected ? 1.5 : 1),
            IsEnabled = !isFuture,
            ToolTip = isFuture ? null : BuildTooltip(date, hasContent),
        };
        cell.Style = (Style)Resources["DayCell"];

        // 用 SetResourceReference 而不是直接赋 Brush：切主题时能跟着换，
        // 而不用等下一次重建面板。
        if (isSelected)
        {
            cell.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
            cell.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
        }
        else if (isToday)
        {
            cell.Background = Brushes.Transparent;
            cell.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
        }
        else
        {
            cell.Background = Brushes.Transparent;
            cell.BorderBrush = Brushes.Transparent;
        }

        var number = new TextBlock
        {
            Text = date.Day.ToString(),
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        number.SetResourceReference(TextBlock.ForegroundProperty,
            isSelected ? "AccentForegroundBrush"
            : !inMonth ? "TextMutedBrush"
            : isWeekend ? "TextSecondaryBrush"
            : "TextPrimaryBrush");
        if (isToday && !isSelected) number.FontWeight = FontWeights.Medium;

        // 数字和圆点上下摞成一坨，整坨在格子里居中。
        //
        // 以前是"数字撑满格子、圆点贴格子底"两个独立对齐 —— 那样两者的间距由
        // 字体行高和格子高度各自决定，谁也算不出来。实测就是这样：数字墨迹落在
        // 512..548（4 倍图），圆点贴底后顶边只在 542，**圆点整个嵌进"8"的下圈**，
        // 把数字笔画盖掉一块。而且就算把圆点底距压到 0，顶边也只到 540，
        // 还是够不着数字底下 —— 36 高的格子里，光移圆点根本走不出去。
        //
        // 改成一坨之后，间距是 Margin 上的 2，**与字体无关的绝对量**；
        // 整坨居中，数字自然上移，两者再也不会撞。
        var dot = new Ellipse
        {
            Width = 5,
            Height = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0),
            // 用 Opacity 而不是 Collapsed：Collapsed 不占位，会让数字回到格子正中，
            // 于是同一行里"有记录"和"没记录"的日子数字高度差 3.5 DIP，一眼就是错位。
            Opacity = hasContent ? 1 : 0,
        };
        dot.SetResourceReference(Shape.FillProperty, isSelected ? "AccentForegroundBrush" : "AccentBrush");

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(number);
        content.Children.Add(dot);
        cell.Content = content;

        // 非本月的日子也允许点，但视觉上退到背景里
        if (!inMonth) cell.Opacity = 0.55;

        var captured = date.Date;
        cell.Click += (_, _) => Pick(captured);

        return cell;
    }

    private string BuildTooltip(DateTime date, bool hasContent)
    {
        var text = date.ToString("yyyy-MM-dd") + " " + ReportService.WeekdayText(date);
        if (date.Date == DateTime.Today) text += "（今天）";
        if (hasContent) text += "\n有记录";
        return text;
    }

    // ------------------------------------------------------------------
    // 「跳到」输入框
    // ------------------------------------------------------------------

    private static readonly Regex ChineseDate = new(
        @"^(?:(?<y>\d{4})\s*年)?\s*(?<m>\d{1,2})\s*月\s*(?<d>\d{1,2})\s*[日号]?$",
        RegexOptions.Compiled);

    private static readonly Regex FullDate = new(
        @"^(?<y>\d{4})\D+(?<m>\d{1,2})\D+(?<d>\d{1,2})$",
        RegexOptions.Compiled);

    private static readonly Regex ShortDate = new(
        @"^(?<m>\d{1,2})\D+(?<d>\d{1,2})$",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析"跳到"输入框里的日期。
    ///
    /// 支持的写法：9/23　9-23　9.23　0923　9月23日　2026-09-23　20260923
    /// 　　　　　　今天／昨天／前天／大前天
    /// 　　　　　　以及只填一个日号（理解为"当前显示的这个月的那一天"）
    ///
    /// 只写月日时如果今年那天还没到，按**上一次**的那天理解 —— 这是个往回翻日志的工具，
    /// 九月里敲「12/25」显然不是想跳到三个月后。
    /// </summary>
    public static bool TryParseJump(string? text, DateTime displayMonth, out DateTime date, out string error)
    {
        date = default;
        error = "";

        var raw = (text ?? "").Trim();
        if (raw.Length == 0) return false;

        var today = DateTime.Today;

        switch (raw)
        {
            case "今天" or "今日" or "today":
                return Accept(today, false, ref date, ref error);
            case "昨天" or "昨日" or "yesterday":
                return Accept(today.AddDays(-1), false, ref date, ref error);
            case "前天":
                return Accept(today.AddDays(-2), false, ref date, ref error);
            case "大前天":
                return Accept(today.AddDays(-3), false, ref date, ref error);
        }

        var cn = ChineseDate.Match(raw);
        if (cn.Success)
        {
            int y = cn.Groups["y"].Success ? int.Parse(cn.Groups["y"].Value) : 0;
            return Build(y, int.Parse(cn.Groups["m"].Value), int.Parse(cn.Groups["d"].Value),
                rollBackYear: y == 0, bareDay: false, out date, out error);
        }

        var full = FullDate.Match(raw);
        if (full.Success)
            return Build(int.Parse(full.Groups["y"].Value),
                int.Parse(full.Groups["m"].Value), int.Parse(full.Groups["d"].Value),
                rollBackYear: false, bareDay: false, out date, out error);

        var shortForm = ShortDate.Match(raw);
        if (shortForm.Success)
            return Build(0, int.Parse(shortForm.Groups["m"].Value), int.Parse(shortForm.Groups["d"].Value),
                rollBackYear: true, bareDay: false, out date, out error);

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == raw.Length)
        {
            switch (digits.Length)
            {
                case 8:   // yyyyMMdd
                    return Build(int.Parse(digits[..4]), int.Parse(digits[4..6]), int.Parse(digits[6..]),
                        rollBackYear: false, bareDay: false, out date, out error);

                case 4:   // MMdd
                    return Build(0, int.Parse(digits[..2]), int.Parse(digits[2..]),
                        rollBackYear: true, bareDay: false, out date, out error);

                case 3:   // Mdd
                    return Build(0, int.Parse(digits[..1]), int.Parse(digits[1..]),
                        rollBackYear: true, bareDay: false, out date, out error);

                case 1 or 2:   // 只填日号 → 当前显示的这个月
                    return Build(displayMonth.Year, displayMonth.Month, int.Parse(digits),
                        rollBackYear: false, bareDay: true, out date, out error);
            }
        }

        error = "看不懂，试试 9/23、0923、2026-09-23 或 昨天";
        return false;
    }

    /// <summary>
    /// year 传 0 表示"没写年份"，此时用今年；若今年那天还没到就退回上一年再试。
    /// </summary>
    private static bool Build(
        int year, int month, int day, bool rollBackYear, bool bareDay,
        out DateTime date, out string error)
    {
        date = default;
        error = "";

        if (month is < 1 or > 12)
        {
            error = $"没有 {month} 月";
            return false;
        }

        var today = DateTime.Today;

        if (rollBackYear)
        {
            int y = today.Year;
            if (!IsValidDate(y, month, day))
            {
                error = $"没有 {y}-{month:00}-{day:00} 这一天";
                return false;
            }

            var candidate = new DateTime(y, month, day);
            if (candidate > today && IsValidDate(y - 1, month, day))
                candidate = new DateTime(y - 1, month, day);

            return Accept(candidate, false, ref date, ref error);
        }

        if (year is < 1900 or > 2200 || !IsValidDate(year, month, day))
        {
            error = $"没有 {year:0000}-{month:00}-{day:00} 这一天";
            return false;
        }

        return Accept(new DateTime(year, month, day), bareDay, ref date, ref error);
    }

    private static bool Accept(DateTime candidate, bool bareDay, ref DateTime date, ref string error)
    {
        if (candidate > DateTime.Today)
        {
            error = bareDay
                ? $"本月 {candidate.Day} 号还没到"
                : "还没到呢，日志里只有过去的日期";
            return false;
        }

        date = candidate;
        error = "";
        return true;
    }

    private static bool IsValidDate(int year, int month, int day)
    {
        if (year is < 1900 or > 2200) return false;
        if (month is < 1 or > 12) return false;
        return day >= 1 && day <= DateTime.DaysInMonth(year, month);
    }

    private void OnJumpTextChanged(object sender, TextChangedEventArgs e) => RefreshJumpHint();

    private void OnJumpKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DismissRequested?.Invoke();
            return;
        }

        if (e.Key != Key.Enter) return;
        e.Handled = true;

        if (!TryParseJump(JumpInput.Text, _displayMonth, out var date, out _))
        {
            RefreshJumpHint();   // 解析失败时把原因摆出来，光标留在框里让用户改
            return;
        }

        Pick(date);
    }

    /// <summary>
    /// 边打字边给反馈：解析成功就显示解析出来的具体日期。
    /// 这一步很重要 —— 「9/23」到底算今年还是去年，光看输入是猜不出来的。
    /// </summary>
    private void RefreshJumpHint()
    {
        if (JumpInput == null || JumpHint == null) return;

        var text = JumpInput.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            JumpHint.Text = "";
            JumpHint.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            return;
        }

        if (TryParseJump(text, _displayMonth, out var date, out var error))
        {
            JumpHint.Text = $"→ {date:yyyy-MM-dd} {ReportService.WeekdayText(date)}";
            JumpHint.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        }
        else
        {
            JumpHint.Text = error;
            JumpHint.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        }
    }

    // ------------------------------------------------------------------

    private void OnPrevMonthClick(object sender, RoutedEventArgs e) => GoMonth(-1);

    private void OnNextMonthClick(object sender, RoutedEventArgs e) => GoMonth(1);

    private void OnTodayClick(object sender, RoutedEventArgs e) => Pick(DateTime.Today);
}
