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
///   · 有东西   —— 数字下面点一个小圆点；传了 counts 就在点旁边再写一个当天数量
///
/// **两种口径共用这一个面板**：
///   · 「有记录」—— 今日记录页用，圆点表示这天有手写记录或归档（<see cref="MainWindow.CollectMarkedDates"/>）
///   · 「有归档」—— 文件归档页用，圆点 + 小数字（那天的归档文件数），见 Show 的 counts 参数
/// 之所以不复制一份出来，是因为格子的几何（36 格宽、圆点 5 DIP、数字+圆点摞成一坨居中）
/// 是被像素核销过的，复制出来的那一份会慢慢走样。
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

    // 非 null = 归档模式。存的是"那天有几个归档文件"，圆点旁边的小数字从这里取。
    private Dictionary<string, int>? _counts;

    // 真正写进格子里那串角标文字（日期 → 文字），每渲染一次重记一次。
    // 诊断断言必须读**这个**，不能回头去调 CountBadge 重算一遍 ——
    // 重算等于拿同一个函数验它自己，改坏了也照样"通过"（实测反验过：把小数字整体 +1，
    // 读数据的断言一声不响，读控件的当场就报）。
    private readonly Dictionary<string, string> _renderedBadges = new(StringComparer.Ordinal);

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

    /// <summary>当天有几个归档文件；非归档模式下返回"有 / 没有"的 1 / 0。诊断自检用。</summary>
    public int CountOf(DateTime date)
        => _counts != null
            ? (_counts.TryGetValue(DateKey(date), out var n) ? n : 0)
            : HasMark(date) ? 1 : 0;

    /// <summary>
    /// 格子上真正写出来的角标文字（空串 = 这天没有东西，或这一格没被渲染）。
    ///
    /// 读的是**渲染时记录下来的那串字**，不是拿 CountOf 重算一遍 ——
    /// 重算的话，"小数字整体 +1"这种改动会让断言的期望值和实际值用同一个函数算出来，
    /// 一起错、一起"通过"。自检反验过这一条：读数据的那个版本对 +1 毫无反应。
    /// 归档模式以外恒为空串。
    /// </summary>
    public string BadgeOf(DateTime date) => _renderedBadges.GetValueOrDefault(DateKey(date), "");

    /// <summary>归档模式下的"日期 → 文件数"表；非归档模式为 null。诊断自检用。</summary>
    public IReadOnlyDictionary<string, int>? Counts => _counts;

    /// <summary>图例那行字（「有记录的日期」/「有归档的日期」）。诊断自检用。</summary>
    public string LegendTextValue => LegendText.Text;

    /// <summary>底部统计那行字。诊断自检用。</summary>
    public string StatsTextValue => StatsText.Text;

    /// <summary>
    /// 打开面板并渲染指定月份。
    ///
    /// markedDates 传"有内容"的日期集合（yyyy-MM-dd），用来画小圆点。
    /// counts 传"日期 → 归档文件数"就是归档模式：圆点旁边多一个小数字，
    /// 图例、统计和 ToolTip 全部换成归档口径（传了 counts 就**不看** markedDates 了，
    /// 两者同时传只会让"圆点在、数字不在"这种不一致有机可乘）。
    /// </summary>
    public void Show(DateTime displayMonth, DateTime selected, IEnumerable<string>? markedDates,
                     IReadOnlyDictionary<string, int>? counts = null)
    {
        _displayMonth = new DateTime(displayMonth.Year, displayMonth.Month, 1);
        _selected = selected.Date;

        _counts = counts == null
            ? null
            : new Dictionary<string, int>(counts, StringComparer.Ordinal);

        _marked = _counts != null
            ? new HashSet<string>(_counts.Keys, StringComparer.Ordinal)
            : markedDates == null
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

    /// <summary>翻月。delta 为 -1 / +1。口径（归档 / 记录）跟着面板一起翻过去。</summary>
    public void GoMonth(int delta) => Show(_displayMonth.AddMonths(delta), _selected, _marked, _counts);

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
        _renderedBadges.Clear();

        var start = GridStart();
        int marked = 0;

        for (int i = 0; i < Rows * Columns; i++)
        {
            var date = start.AddDays(i);
            if (_marked.Contains(DateKey(date))) marked++;
            DayGrid.Children.Add(CreateDayCell(date));
        }

        // 图例和统计都得跟着口径走：归档页里写"有记录的日期"是错的 ——
        // 那一天可能一条记录都没有，只有几个文件。
        LegendText.Text = IsArchiveMode ? "有归档的日期" : "有记录的日期";

        StatsText.Text = marked == 0
            ? $"今天 {DateTime.Today:M/d}"
            : $"今天 {DateTime.Today:M/d} · {marked} 天{(IsArchiveMode ? "有归档" : "有记录")}";

        RefreshJumpHint();
    }

    /// <summary>当前是不是"归档计数"口径（圆点旁边还有数字）。</summary>
    private bool IsArchiveMode => _counts != null;

    /// <summary>这一天算不算"有东西"（两个口径通用）。</summary>
    private bool HasMark(DateTime date) => _marked.Contains(DateKey(date));

    /// <summary>日期的字符串键，用来和「有记录」集合比对。</summary>
    private static string DateKey(DateTime date) => date.ToString("yyyy-MM-dd");

    private UIElement CreateDayCell(DateTime date)
    {
        bool inMonth = date.Month == _displayMonth.Month && date.Year == _displayMonth.Year;
        bool isToday = date.Date == DateTime.Today;
        bool isSelected = date.Date == _selected;
        bool isFuture = date.Date > DateTime.Today;
        bool isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        int count = CountOf(date);

        var cell = new Button
        {
            Width = 36,
            Height = 36,
            Margin = new Thickness(1),
            BorderThickness = new Thickness(isToday && !isSelected ? 1.5 : 1),
            IsEnabled = !isFuture,
            ToolTip = isFuture ? null : BuildTooltip(date, count),
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

        // 数字和圆点（归档模式下还有数量）上下摞成一坨，整坨在格子里居中。
        //
        // 以前是"数字撑满格子、圆点贴格子底"两个独立对齐 —— 那样两者的间距由
        // 字体行高和格子高度各自决定，谁也算不出来。实测就是这样：数字墨迹落在
        // 512..548（4 倍图），圆点贴底后顶边只在 542，**圆点整个嵌进"8"的下圈**，
        // 把数字笔画盖掉一块。而且就算把圆点底距压到 0，顶边也只到 540，
        // 还是够不着数字底下 —— 36 高的格子里，光移圆点根本走不出去。
        //
        // 改成一坨之后，间距是 Margin 上的 1，**与字体无关的绝对量**；
        // 整坨居中，数字自然上移，两者再也不会撞。
        // 归档模式在圆点右边接一个小数字，仍然在这一坨里：宽度方向由 StackPanel 自己算，
        // 不会撑到格子外面去（撑出去就会被布局套一个矩形 clip，把圆角切平）。
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(number);
        content.Children.Add(BuildMarkRow(date, isSelected, count));
        cell.Content = content;

        // 非本月的日子也允许点，但视觉上退到背景里
        if (!inMonth) cell.Opacity = 0.55;

        var captured = date.Date;
        cell.Click += (_, _) => Pick(captured);

        return cell;
    }

    /// <summary>
    /// 数字下面那一行：一个圆点；归档模式下圆点右边再跟一个当天的归档数量。
    ///
    /// 藏起来的时候用 Opacity 而不是 Collapsed —— Collapsed 不占位，
    /// 会让数字回到格子正中，于是同一行里"有"和"没有"的日子数字高度差 3.5 DIP，
    /// 一眼就是错位。**整行一起透明**（点点和数字是一体，只藏数字会剩一个孤零零的点）。
    ///
    /// 顺手把真正写出去的那串角标文字记进 <c>_renderedBadges</c>，给诊断侧核销用。
    /// </summary>
    private UIElement BuildMarkRow(DateTime date, bool isSelected, int count)
    {
        var brush = isSelected ? "AccentForegroundBrush" : "AccentBrush";

        if (!IsArchiveMode)
        {
            // 今日记录页那套：只有一个点。这条路径的几何是量过像素核销过的（见上面的长注释），
            // 除了取值来源以外一个数都没动。
            var lone = new Ellipse
            {
                Width = 5,
                Height = 5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
                Opacity = count > 0 ? 1 : 0,
            };
            lone.SetResourceReference(Shape.FillProperty, brush);
            return lone;
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0),
            Opacity = count > 0 ? 1 : 0,
        };

        var dot = new Ellipse
        {
            Width = 5,
            Height = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dot.SetResourceReference(Shape.FillProperty, brush);
        row.Children.Add(dot);

        // 8.5 号字是为了让"数字 + 5 DIP 点"整行不超过格子宽度：
        // 两位数最宽也就 15 个 DIP 左右，加上点和间距一共 22，离 34 的内容区还很远。
        // 数字本身封顶两位（99+），见 DayIndex.CountBadge。
        var badge = new TextBlock
        {
            Text = DayIndex.CountBadge(count),
            FontSize = 8.5,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge.SetResourceReference(TextBlock.ForegroundProperty, brush);
        row.Children.Add(badge);

        // 记的是**控件上那串字**（而不是刚才算出来的 count）：改这里的赋值，
        // 诊断那边读到的就会跟着变，断言拦得住。
        _renderedBadges[DateKey(date)] = badge.Text;

        return row;
    }

    /// <summary>
    /// 格子上的提示语。归档模式下必须写清"几个文件"——
    /// 数字封顶在 99+，说到具体数量只能靠这里。
    /// </summary>
    private string BuildTooltip(DateTime date, int count)
    {
        var text = date.ToString("yyyy-MM-dd") + " " + ReportService.WeekdayText(date);
        if (date.Date == DateTime.Today) text += "（今天）";
        if (count <= 0) return text;

        return text + (IsArchiveMode ? $"\n{count} 个归档文件" : "\n有记录");
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
