using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WhaleLog.Models;
using WhaleLog.Services;

namespace WhaleLog.Views;

/// <summary>主面板的三个页签。</summary>
public enum NavPage
{
    Notes,
    Files,
    Settings,
}

/// <summary>
/// 主面板：今日记录 / 文件归档 / 设置 三页。
///
/// 用代码后置而不是完整 MVVM —— 这个界面基本是一对一的控件映射，
/// 硬套 ViewModel 只会多出几百行转发代码。真正需要双向同步的地方
/// （勾选框、分类胶囊）都做了 _syncing 保护，避免程序化赋值反过来触发保存。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<NoteEntry> _entries = new();
    private readonly ObservableCollection<FileRecord> _files = new();

    /// <summary>
    /// 今日记录页里那份「当天归档的文件」。
    ///
    /// 刻意和 _files 分开：_files 受文件归档页的「只看当天 / 全部」筛选影响，
    /// 而这一份永远只跟当前查看的日期走，两边的口径不该互相干扰。
    /// </summary>
    private readonly ObservableCollection<FileRecord> _dayFiles = new();
    private readonly List<ToggleButton> _categoryChips = new();
    private readonly DispatcherTimer _debounce;

    private DateTime _currentDate = DateTime.Today;
    private string _selectedCategory = NoteCategories.Work;
    private bool _syncing;
    private bool _showAllFiles;

    /// <summary>文件归档页的搜索关键字（已去空白，空串 = 不筛）。</summary>
    private string _fileKeyword = "";

    /// <summary>程序化写搜索框时立起来，避免 TextChanged 反过来再跑一遍筛选。</summary>
    private bool _syncingSearch;

    private Action? _pendingAction;

    public MainWindow()
    {
        InitializeComponent();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            var action = _pendingAction;
            _pendingAction = null;
            try { action?.Invoke(); }
            catch { /* 自动保存失败不打断输入 */ }
        };

        EntryList.ItemsSource = _entries;
        FileList.ItemsSource = _files;
        DayFileList.ItemsSource = _dayFiles;

        BuildCategoryChips();
        LoadSettingsToUi();
        LoadDay(_currentDate);
        RefreshFileList();
        RefreshStats();

        // Slider 的 ValueChanged 不写在 XAML 里：解析 XAML 时给 Minimum/Maximum 赋值
        // 会让 Value 被强制转换，从而触发 ValueChanged —— 那一刻同一段 XAML 里排在
        // Slider 后面的 TextBlock 还没构造出来，处理器一访问就是空引用。
        // 放到这里订阅，既保证控件齐全，也顺便避开了 LoadSettingsToUi 的程序化赋值。
        ScaleSlider.ValueChanged += OnFloatingSliderChanged;
        OpacitySlider.ValueChanged += OnFloatingSliderChanged;

        // 月历里选日期和日期栏的「‹ › 今天」走的是同一个 LoadDay，行为保持一致
        MonthPanel.DateChosen += OnMonthPanelDateChosen;
        MonthPanel.DismissRequested += () => DatePopup.IsOpen = false;
        RecentPanel.DateChosen += OnRecentPanelDateChosen;
        RecentPanel.DismissRequested += () => RecentPopup.IsOpen = false;

        // 归档日历和今日记录页的月历是同一个控件，但选中的后果不同：
        // 那边是"换一天看记录"，这边是"把文件列表滚到那一天的那一组"。
        FileCalendarPanel.DateChosen += OnFileCalendarPanelDateChosen;
        FileCalendarPanel.DismissRequested += () => FileCalendarPopup.IsOpen = false;

        PreviewKeyDown += OnWindowPreviewKeyDown;

        DataPathText.Text = AppPaths.Root;
        AboutIcon.Source = IconAssets.LoadPreviewImage();
        AboutVersionText.Text = BuildVersionLine();

        App.Store.Changed += OnStoreChanged;

        PreviewDragEnter += OnDragEnter;
        PreviewDragOver += OnDragOver;
        PreviewDragLeave += OnDragLeave;
        PreviewDrop += OnDrop;
    }

    private static string BuildVersionLine()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        var date = File.GetLastWriteTime(Environment.ProcessPath ?? "").ToString("yyyy-MM-dd");
        return $"版本 {v?.ToString(3) ?? "1.0.0"} · 构建于 {date}";
    }

    protected override void OnClosed(EventArgs e)
    {
        App.Store.Changed -= OnStoreChanged;
        DetachEntries();

        // 防抖里的进度可能还没落盘。关掉主面板不等于程序退出（托盘还在，计时器还会跑），
        // 但这里顺手结清更保险 —— 谁也不想拖完进度条随手关窗就丢了。
        _debounce.Stop();
        var pending = _pendingAction;
        _pendingAction = null;
        try { pending?.Invoke(); }
        catch { /* 窗口正在关闭，落盘失败也不该再抛出去 */ }

        base.OnClosed(e);
    }

    // ==================================================================
    // 页签
    // ==================================================================

    /// <summary>当前页签。三个页面的可见性全部由它推导，不看 ToggleButton 各自的勾选状态。</summary>
    private NavPage _activePage = NavPage.Notes;

    public NavPage ActivePage => _activePage;

    public int VisiblePageCount =>
        new FrameworkElement[] { PageNotes, PageFiles, PageSettings }
            .Count(p => p.Visibility == Visibility.Visible);

    public string NavStateReport =>
        $"今日记录={PageNotes.Visibility} 文件归档={PageFiles.Visibility} 设置={PageSettings.Visibility}";

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        // ToggleButton 之间不会互斥，而且再点一次当前页签会把 IsChecked 反选成 false。
        // 所以这里只认两件事：勾上的那个就是要切过去的页；如果是把自己点掉了，
        // 就维持当前页不变，避免出现"三页全隐藏"的空白面板。
        SwitchTo(clicked.IsChecked == true ? PageOf(clicked) : _activePage);
    }

    private NavPage PageOf(ToggleButton button)
        => ReferenceEquals(button, NavFiles) ? NavPage.Files
         : ReferenceEquals(button, NavSettings) ? NavPage.Settings
         : NavPage.Notes;

    /// <summary>
    /// 切换到指定页。
    ///
    /// 关键点：三个页面的可见性必须由同一个 source of truth（page）推导。
    /// 之前是各自读自己的 ToggleButton.IsChecked，一旦勾选状态没互斥，
    /// 就会出现两个页面同时 Visible —— 而这三个页面是叠在同一个 Grid 单元格里的，
    /// 表现就是内容完全重叠。
    /// </summary>
    public void SwitchTo(NavPage page)
    {
        _activePage = page;

        _syncing = true;
        NavNotes.IsChecked = page == NavPage.Notes;
        NavFiles.IsChecked = page == NavPage.Files;
        NavSettings.IsChecked = page == NavPage.Settings;

        PageNotes.Visibility = page == NavPage.Notes ? Visibility.Visible : Visibility.Collapsed;
        PageFiles.Visibility = page == NavPage.Files ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = page == NavPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        _syncing = false;

        if (page == NavPage.Files) RefreshFileList();
        if (page == NavPage.Settings) LoadSettingsToUi();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Settings.HideMainWindowOnClose)
        {
            Hide();
            return;
        }
        Application.Current.Shutdown();
    }

    // ==================================================================
    // 今日记录
    // ==================================================================

    private void BuildCategoryChips()
    {
        CategoryChips.Children.Clear();
        _categoryChips.Clear();

        foreach (var cat in NoteCategories.All)
        {
            var chip = new ToggleButton
            {
                Content = cat,
                IsChecked = cat == _selectedCategory,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(11, 5, 11, 5),
            };
            chip.SetResourceReference(StyleProperty, "ChipToggle");
            chip.Click += OnCategoryChipClick;
            CategoryChips.Children.Add(chip);
            _categoryChips.Add(chip);
        }
    }

    private void OnCategoryChipClick(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        if (sender is not ToggleButton clicked) return;
        _selectedCategory = clicked.Content as string ?? NoteCategories.Work;

        // 单选：其它胶囊全部取消勾选
        _syncing = true;
        foreach (var chip in _categoryChips)
            chip.IsChecked = ReferenceEquals(chip, clicked);
        _syncing = false;
    }

    private void OnPrevDayClick(object sender, RoutedEventArgs e) => LoadDay(_currentDate.AddDays(-1));

    private void OnNextDayClick(object sender, RoutedEventArgs e)
    {
        var next = _currentDate.AddDays(1);
        if (next > DateTime.Today) return;
        LoadDay(next);
    }

    private void OnTodayClick(object sender, RoutedEventArgs e) => LoadDay(DateTime.Today);

    /// <summary>
    /// 跳到相邻的"有记录的日子"（Alt + ← / →，也就是往期清单里 ↑↓ 的落点规则）。
    ///
    /// 返回 false 表示这个方向上再没有有记录的日子了 —— 界面据此说一句话，
    /// 而不是默默不动（"按了没反应"和"确实没有了"是两件事，用户分不出来）。
    /// </summary>
    public bool GoToAdjacentDayWithContent(int direction)
    {
        var days = DayIndex.Build(App.Store.Book.Days, App.Store.Book.Files);
        var target = DayIndex.Adjacent(days, _currentDate, direction);
        if (target == null)
        {
            SetStatus(direction < 0 ? "前面再没有记过东西的日子了" : "后面再没有记过东西的日子了");
            return false;
        }

        LoadDay(target.Value);
        SetStatus($"已跳到 {target.Value:yyyy-MM-dd} {ReportService.WeekdayText(target.Value)}" +
                  (direction < 0 ? "（上一个有记录的日子）" : "（下一个有记录的日子）"));
        return true;
    }

    /// <summary>切到指定日期。日期栏「‹ ›」、月历、跳转框都收敛到 LoadDay 这一个入口。</summary>
    public void GoToDate(DateTime date) => LoadDay(date);

    /// <summary>当前正在查看的日期（诊断自检用）。</summary>
    public DateTime ViewingDate => _currentDate;

    // ------------------------------------------------------------------
    // 整月日历
    // ------------------------------------------------------------------

    private DateTime _datePopupClosedAt = DateTime.MinValue;

    private void OnDateButtonClick(object sender, RoutedEventArgs e)
    {
        if (DatePopup.IsOpen)
        {
            DatePopup.IsOpen = false;
            return;
        }

        // StaysOpen=False 的弹出层在点到触发按钮自身时会先关闭自己，
        // 那一次点击不会再走到这里。加个时间窗，避免"刚关掉又被立刻打开"。
        if ((DateTime.UtcNow - _datePopupClosedAt).TotalMilliseconds < 260) return;

        OpenDatePicker();
    }

    /// <summary>打开月历，定位到当前选中日期所在的月份。</summary>
    public void OpenDatePicker()
    {
        MonthPanel.Show(_currentDate, _currentDate, CollectMarkedDates());
        DatePopup.IsOpen = true;

        // 焦点直接落进「跳到」输入框：打开面板就能敲日期，也能用 Esc 收起
        MonthPanel.FocusJumpInput();
    }

    private void OnDatePopupClosed(object? sender, EventArgs e)
        => _datePopupClosedAt = DateTime.UtcNow;

    // ------------------------------------------------------------------
    // 往期清单
    // ------------------------------------------------------------------

    private DateTime _recentPopupClosedAt = DateTime.MinValue;

    /// <summary>往期清单面板实例。诊断自检用。</summary>
    public RecentDaysPanel RecentDays => RecentPanel;

    /// <summary>往期清单里当前有几行（等于筛出来的"有记录的日子"数）。诊断自检用。</summary>
    public int RecentDayRowCount => RecentPanel.RowCount;

    private void OnRecentButtonClick(object sender, RoutedEventArgs e)
    {
        if (RecentPopup.IsOpen)
        {
            RecentPopup.IsOpen = false;
            return;
        }

        // 和月历同一套：StaysOpen=False 的弹出层被自己的触发按钮点到时会先自关，
        // 那一次点击不会再走到这里，所以加个时间窗避免"刚关掉又被立刻打开"。
        if ((DateTime.UtcNow - _recentPopupClosedAt).TotalMilliseconds < 260) return;

        OpenRecentDays();
    }

    /// <summary>把数据灌进往期清单并打开它。诊断自检走的是同一条路。</summary>
    public void OpenRecentDays()
    {
        RefreshRecentDays();
        RecentPopup.IsOpen = true;
        RecentPanel.FocusFilterInput();
    }

    /// <summary>
    /// 重新灌一次往期清单的数据（不打开弹出层）。
    ///
    /// 每次打开都重算而不是常驻：清单依赖的是整库的汇总，
    /// 缓存一份就要在"归档了新文件""删了记录""换了日期"每个入口都记得刷新它 —— 漏一处就是脏数据。
    /// </summary>
    public void RefreshRecentDays()
        => RecentPanel.Show(_currentDate, DayIndex.Build(App.Store.Book.Days, App.Store.Book.Files));

    private void OnRecentPopupClosed(object? sender, EventArgs e)
        => _recentPopupClosedAt = DateTime.UtcNow;

    /// <summary>往期清单里选中某一天：切过去并收起弹出层。</summary>
    private void OnRecentPanelDateChosen(DateTime date)
    {
        RecentPopup.IsOpen = false;
        LoadDay(date);
        SetStatus($"已跳到 {date:yyyy-MM-dd} {ReportService.WeekdayText(date)}");
    }

    /// <summary>
    /// 哪些日子有内容（手写记录或归档文件），用来在月历上点小圆点。
    ///
    /// 公开且静态：诊断侧要把月历单独渲染成图核销（小圆点的位置只能看图判断），
    /// 而"哪天算有记录"必须**只有这一处判定** —— 诊断自己再抄一遍的话，
    /// 哪天改了规则就会出现"图上没有圆点、界面上有"这种最误导人的局面。
    ///
    /// 判定本身搬到了 <see cref="DayIndex.Build"/>（纯逻辑、能自测、往期清单也用同一份结果），
    /// 这里只是把索引取出来 —— 留着这个入口是为了让"月历上的圆点"和
    /// "往期清单里的行"永远是同一批日子。
    /// </summary>
    public static IEnumerable<string> CollectMarkedDates()
        => DayIndex.Build(App.Store.Book.Days, App.Store.Book.Files).Select(d => d.Date);

    /// <summary>
    /// 窗口级快捷键：
    ///   Esc           收起月历 / 往期清单 / 归档日历（StaysOpen=False 只管点击外部，不管键盘）
    ///   Alt + ← / →   跳到上一个 / 下一个**有记录**的日子
    ///
    /// 后者的理由是「‹」只能一天一天退：从今天退到两周前要点十几次，中间还夹着空白日。
    /// 用 Alt 而不是裸方向键，是因为裸方向键在输入框里要留给光标移动。
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (DatePopup.IsOpen || RecentPopup.IsOpen || FileCalendarPopup.IsOpen))
        {
            DatePopup.IsOpen = false;
            RecentPopup.IsOpen = false;
            FileCalendarPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.Left || e.Key == Key.Right) &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            e.Handled = true;
            GoToAdjacentDayWithContent(e.Key == Key.Left ? -1 : 1);
        }
    }

    /// <summary>月历里选中某一天：切过去并收起弹出层。</summary>
    private void OnMonthPanelDateChosen(DateTime date)
    {
        DatePopup.IsOpen = false;
        LoadDay(date);
        SetStatus($"已切换到 {date:yyyy-MM-dd} {ReportService.WeekdayText(date)}");
    }

    private void LoadDay(DateTime date)
    {
        _currentDate = date;
        var day = App.Store.GetOrCreateDay(date);

        _syncing = true;
        DetachEntries();
        _entries.Clear();
        foreach (var entry in day.Entries.OrderBy(x => x.CreatedAt))
        {
            AttachEntry(entry);
            _entries.Add(entry);
        }

        SummaryInput.Text = day.Summary;
        DateText.Text = date == DateTime.Today ? $"今天 · {date:MM月dd日 dddd}" : $"{date:yyyy年MM月dd日 dddd}";
        _syncing = false;

        UpdateDropBanner();
        RefreshStats();
        RefreshFileList();
    }

    // ==================================================================
    // 进度条的落盘
    //
    // 进度条靠 TwoWay 绑定直接写进 NoteEntry.Progress，控件那边不需要任何事件 ——
    // 但"什么时候存盘"得有人管，而且拖动是连续的，不能动一下写一次 index.json。
    // 所以在这里订阅每个条目的 PropertyChanged，统一走已有的 700ms 防抖。
    // ==================================================================

    private void AttachEntry(NoteEntry entry) => entry.PropertyChanged += OnEntryPropertyChanged;

    private void DetachEntry(NoteEntry entry) => entry.PropertyChanged -= OnEntryPropertyChanged;

    private void DetachEntries()
    {
        foreach (var entry in _entries) DetachEntry(entry);
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || _probeMode) return;
        // 只认进度条。勾选框那边有 OnEntryDoneChanged 自己那条路，两边都管就会写两遍。
        if (e.PropertyName != nameof(NoteEntry.Progress)) return;

        // 表头的"进行中 / 已完成"要立刻跟上，落盘可以慢一点（拖动是连续的，动一下写一次太浪费）
        RefreshStats();
        Debounce(() => App.Store.Save());

        // 回看历史时把一条改回"没做完" → 今天那边要立刻能看到（勾选框取消勾选也走这里：
        // Done 的 setter 会把 Progress 退回 ResumeProgress，同样会抛出这条通知）。
        TrySyncUnfinishedPastEntryToToday(sender as NoteEntry);
    }

    /// <summary>
    /// 过去某天的记录被改成未完成 → 立刻把它同步（顺延）到今天。返回是否真的同步了。
    ///
    /// 为什么必须单独有这一步：自动顺延（<see cref="CarryOverService.RunOnBook"/>）只在
    /// **启动**和**跨零点**跑。可"昨天那条其实没干完"这种更正，常常是第二天翻记录时才发现的 ——
    /// 那时候早就过了顺延的时机，不在这儿补一下，今天这边一动不动，用户看到的就是
    /// "我明明改了，它没过来"，而且只能等下次开机才冒出来。
    ///
    /// 前置过滤（过去的日子 / 没完成 / 没顺延过）在这里判一次，判定本身在
    /// <see cref="CarryOverService.CarryEntry"/> 里再判一次 —— 那边是唯一权威，这里只是
    /// 别去白跑一趟。真正搬的时候**今天的进展优先**：今天已经有这条副本就什么都不做，
    /// 绝不因为回看历史把今天已经做完的事退回去。
    ///
    /// 自检模式（<c>_probeMode</c>）下只走到"该不该同步"这一步就返回：探针要验的是这个判定，
    /// 而它手上的是假数据、背后却是用户的真实记录 —— 不能真去写。
    /// </summary>
    internal bool TrySyncUnfinishedPastEntryToToday(NoteEntry? entry)
    {
        if (entry is not { } e) return false;
        if (_currentDate.Date >= DateTime.Today) return false;          // 只有回看过去才谈得上"同步到今天"
        if (e.Done) return false;                                       // 又勾上了：不用带
        if (!string.IsNullOrEmpty(e.CarriedToDate)) return false;        // 已经顺延过了

        if (_probeMode) return true;

        var report = CarryOverService.CarryEntry(App.Store.Book, _currentDate, e);
        if (!report.Any) return false;

        App.Store.Save();
        SetStatus($"「{e.Text}」还没做完，已同步到今天 {DateTime.Today:M月d日}");

        return true;
    }

    /// <summary>
    /// 把事项列表按数据源重新对齐（顺延过来的新条目就是这时候冒出来的）。
    ///
    /// 刻意不做整表重建：LoadDay 会把 SummaryInput.Text 覆盖成落盘的值，
    /// 而用户可能正在小结框里打字、防抖还没写盘 —— 那样会把他刚打的字吃掉。
    /// 这里只动列表本身。
    /// </summary>
    public void RefreshCurrentDay()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshCurrentDay));
            return;
        }

        var day = App.Store.GetOrCreateDay(_currentDate);
        var wanted = day.Entries.OrderBy(x => x.CreatedAt).ToList();

        _syncing = true;

        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (wanted.Contains(_entries[i])) continue;
            DetachEntry(_entries[i]);
            _entries.RemoveAt(i);
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < _entries.Count && ReferenceEquals(_entries[i], wanted[i])) continue;

            int existing = _entries.IndexOf(wanted[i]);
            if (existing >= 0)
                _entries.Move(existing, i);
            else
            {
                AttachEntry(wanted[i]);
                _entries.Insert(i, wanted[i]);
            }
        }

        _syncing = false;

        RefreshStats();
    }

    /// <summary>
    /// 拖入的文件归属「当前正在查看的那一天」，不是字面的今天 —— 这样才能补录过去某天的文件。
    /// 但这个规则光看界面是猜不到的：在看 9/7 时拖文件，它会被记到 9/7，回到今天当然找不到。
    /// 所以目标日期必须写在横幅上。
    /// </summary>
    private void UpdateDropBanner()
    {
        bool isToday = _currentDate.Date == DateTime.Today;

        DropBannerTitle.Text = isToday
            ? "把今天的文件拖到这里"
            : $"把文件拖到这里 → 记入 {_currentDate:M月d日}";

        DropBannerHint.Text = isToday
            ? "自动识别类别、记录所在位置并标记版本。原文件不会被移动或修改。"
            : $"归入 {_currentDate:yyyy年M月d日} 的记录，不是今天。想看今天的文件先切回「回到今天」。";
    }

    private void OnEntryInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            AddEntry();
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 &&
                 Keyboard.Modifiers == ModifierKeys.None &&
                 !EntryInput.AcceptsReturn)
        {
            e.Handled = true;
            AddEntry();
        }
    }

    private void OnAddEntryClick(object sender, RoutedEventArgs e) => AddEntry();

    private void AddEntry()
    {
        var text = EntryInput.Text.Trim();
        if (text.Length == 0)
        {
            EntryInput.Focus();
            return;
        }

        var day = App.Store.GetOrCreateDay(_currentDate);
        var entry = new NoteEntry
        {
            Text = text,
            Category = _selectedCategory,
            CreatedAt = _currentDate == DateTime.Today
                ? DateTime.Now
                : _currentDate.AddHours(9),
        };

        _syncing = true;
        day.Entries.Add(entry);
        AttachEntry(entry);
        _entries.Add(entry);
        EntryInput.Clear();
        _syncing = false;

        App.Store.Save();
        RefreshStats();
        EntryInput.Focus();
        SetStatus($"已记录：{Truncate(text, 40)}");
    }

    private void OnEntryDoneChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.Store.Save();
        RefreshStats();
    }

    private void OnDeleteEntryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NoteEntry entry) return;

        var day = App.Store.GetOrCreateDay(_currentDate);
        day.Entries.Remove(entry);

        _syncing = true;
        DetachEntry(entry);
        _entries.Remove(entry);
        _syncing = false;

        App.Store.Save();
        RefreshStats();
        SetStatus("已删除一条记录");
    }

    private void OnEditEntryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NoteEntry entry) return;
        OpenEntryEditor(entry);
    }

    /// <summary>
    /// 双击整行也能进编辑。
    ///
    /// 刻意避开落在勾选框 / 进度条 / 按钮上的双击：那几处的双击本来就有别的意思
    /// （勾选框双击等于来回切两次、滑块双击会把值顶到中间），抢过来只会让它们变得不可预测。
    /// </summary>
    private void OnEntryRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not Border row || row.DataContext is not NoteEntry entry) return;
        if (HitsInteractiveControl(e.OriginalSource as DependencyObject, row)) return;

        e.Handled = true;
        OpenEntryEditor(entry);
    }

    /// <summary>
    /// 从点击落点往上走，路上有没有勾选框 / 滑块 / 文本框 / 按钮。
    ///
    /// internal 是为了自检：这条判定只能拿真渲染出来的可视树去验，
    /// 自己拼一棵假树测不出「真实行里那一处落点算不算交互控件」。
    /// </summary>
    internal static bool HitsInteractiveControl(DependencyObject? source, DependencyObject stopAt)
    {
        for (var node = source; node != null && node != stopAt; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase || node is Slider || node is TextBox) return true;
        }
        return false;
    }

    /// <summary>
    /// 打开编辑窗改这条记录。
    ///
    /// 改的是模型里**同一个对象**，所以列表靠绑定自己刷新 —— 不重建列表、不新增记录，
    /// 原来的时间、Id、进度和顺延标记都留着。落盘和状态提示由这里统一做。
    /// </summary>
    private void OpenEntryEditor(NoteEntry entry)
    {
        // 自检会往列表里灌假数据。那会儿真弹一个模态窗出来，自检就卡在那一行等人点确定 ——
        // 表现为"跑到一半不动了"，而且不带 --quiet 时根本看不出是这里。
        if (_probeMode) return;

        var dialog = new EditEntryWindow(entry, _currentDate) { Owner = this };
        dialog.ShowDialog();

        if (!dialog.Saved) return;      // 取消了就什么都别做，一个字节都不写

        ApplyEntryEdit(entry);
    }

    /// <summary>
    /// 编辑窗按下保存之后要干的事 —— 只有这一份，窗口和自检走的都是它。
    ///
    /// 抽出来是为了自检能真跑一遍：编辑窗是模态的（ShowDialog），
    /// 探针里没法"点一下保存再让流程自己往下走"，只能把这段单独拎出来测 ——
    /// 不然这段接线（同步到底调没调）就只能靠肉眼读代码。
    ///
    /// <paramref name="persist"/> 只给探针用：自检会拿**真实数据目录**也跑一遍，
    /// 那时往索引里写一笔假的就等于动用户的记录。探针传 false，逻辑照跑、一个字节不落盘。
    /// </summary>
    internal void ApplyEntryEdit(NoteEntry entry, bool persist = true)
    {
        // 改动顺着顺延链传下去：这条任务从哪天开始、被搬过几天，链上那几份就一起改
        // （双向，编最早那份和编今天这份一样）。只同步正文/分类/标签 ——
        // 进度和勾选状态不碰，链上各天是各自推进的。
        var sync = CarryOverService.SyncContent(App.Store.Book, entry);

        if (persist) App.Store.Save();
        RefreshStats();

        // 同步动的是**别的日期**的记录，用户眼前这一天看不出任何变化 —— 必须说一声。
        SetStatus(EditStatusText(entry.Text, sync));
    }

    /// <summary>
    /// 编辑保存后的那行状态提示。
    ///
    /// 单独一个函数是为了让自检能核这句文案：同步发生在别的日期上，
    /// 界面上一点痕迹都没有，这句话是用户唯一能知道"动过别处数据"的地方 ——
    /// 真要是哪天漏了 Describe()，功能照跑、自检也不该放过。
    /// </summary>
    internal static string EditStatusText(string text, CarryOverService.SyncReport sync) =>
        $"已修改：{Truncate(text, 40)}{sync.Describe()}";

    private void OnSummaryChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        var text = SummaryInput.Text;
        Debounce(() =>
        {
            App.Store.GetOrCreateDay(_currentDate).Summary = text;
            App.Store.Save();
        });
    }

    /// <summary>
    /// 重新套用外部素材（用户在 Assets\ 里换了图之后用）。
    /// 取不到就保留原图，不要把 Source 赋成 null。
    /// </summary>
    public void RefreshAssets()
    {
        var head = IconAssets.Load("whale-head");
        if (head != null)
        {
            TitleIcon.Source = head;
            DropBannerIcon.Source = head;
            DropOverlayIcon.Source = head;
        }

        var preview = IconAssets.LoadPreviewImage();
        if (preview != null) AboutIcon.Source = preview;
    }

    private void RefreshStats()
    {
        var day = App.Store.Book.FindDay(_currentDate);
        int open = day?.OpenCount ?? 0;
        int done = day?.DoneCount ?? 0;
        int files = App.Store.FilesOnDate(_currentDate).Count();

        StatOpen.Text = $"进行中 {open}";
        StatDone.Text = $"已完成 {done}";
        StatFiles.Text = $"归档文件 {files}";
        StatusRight.Text = App.Reminder.RemainingText;

        EntryEmptyHint.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RefreshDayCompletion();
    }

    /// <summary>
    /// 列表底部那行「当天完成度」。
    ///
    /// 口径只有一份，在 Models 的 DayCompletion 里（今天实时算、过去的日子用收工定格值），
    /// 这里只负责把那个数字贴到控件上 —— 数字来源是**界面上真正显示的那些条目**（_entries），
    /// 不是回头去数据源再捞一遍：自检往列表里灌假数据时，不会把用户的真实记录算进来。
    ///
    /// 它的失效方式很安静：行没画出来、数字和上面几条对不上、颜色全绿、文字跑到中间去 ——
    /// 都不会报错，只是"看着不对"。所以自检里逐个控件核（见 DiagnosticsService 的「当天完成度」）。
    /// </summary>
    private void RefreshDayCompletion()
    {
        var day = App.Store.Book.FindDay(_currentDate);
        bool isToday = _currentDate.Date == DateTime.Today;
        int? live = DayCompletion.Average(_entries);

        // 自检模式下定格值（属于真实数据）不能混进来，否则量出来的数字每次都不一样
        int? frozen = _probeMode ? _probeFrozen : day?.Completion;
        int? shown = DayCompletion.Display(live, frozen, isToday);

        if (shown == null)
        {
            // 一条记录都没有的一天没有这个数字 —— 收起整行，
            // 而不是显示个 0%（"今天还没记"和"今天做到 0%"是两回事）
            DayCompletionRow.Visibility = Visibility.Collapsed;
            return;
        }

        DayCompletionRow.Visibility = Visibility.Visible;

        // 过去的日子被改动过（拖了进度条 / 勾了 / 加了一条 / 删了一条）→ 重新定格。
        // 不这么做的话，上面几根条已经是新样子、底下这个数字还停在老样子，看着像坏了。
        // 改动本身就是用户的意图，把结账单改到与事实一致才是诚实的做法。
        if (!isToday && !_probeMode && day != null && day.Completion != live && day.Freeze(DateTime.Now))
            Debounce(() => App.Store.Save());

        var value = shown.Value;

        DayCompletionValue.Text = $"{value}%";

        bool perfect = value >= 100;
        // 注意属性要用各自控件自己的那个 DP：TextBlock.Foreground 和 Control.Foreground
        // 是两个不同的依赖属性，在 TextBlock 上设 Control.Foreground 是**没有视觉效果的**
        // （不报错、也不变色，属于最安静的那种坏法）。
        DayCompletionValue.SetResourceReference(
            TextBlock.ForegroundProperty, perfect ? "SuccessBrush" : "AccentBrush");

        DayCompletionChip.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        DayCompletionChipText.SetResourceReference(
            TextBlock.ForegroundProperty, isToday ? "AccentBrush" : "TextSecondaryBrush");
        DayCompletionChipText.Text = isToday ? "实时更新" : "收工定格";

        // 「今天这个数会变、过去的日子已经定死」这条规则光看界面是猜不到的，写在提示里。
        // 时间戳只在真的在零点附近结过账时才有（补算历史的日子没有），所以两种写法要分开，
        // 不能编一个"定格时间"出来 —— 那会让人以为那天半夜真的结过账。
        DayCompletionRow.ToolTip = isToday
            ? "今天所有事项进度的平均（勾选记 100%），跟着上面每一条实时更新。"
            : day is { HasFrozenTime: true }
                ? $"这一天收工时的完成度：所有事项进度的平均（勾选记 100%）。已于 {day.CompletionFrozenText} 定格，"
                  + "翻回来看到的永远是当时的数字；重新改动这天的记录会再定格一次。"
                : "这一天收工时的完成度：所有事项进度的平均（勾选记 100%）。"
                  + (day is { IsCompletionSettled: true } ? "没有记下收工时刻（老记录或事后补算），" : "")
                  + "显示的是按这天的记录算出来的数字；改动这天的记录会跟着重算。";
    }

    // ==================================================================
    // 文件归档
    // ==================================================================

    /// <summary>文件归档页的筛选：false = 只看当前查看的那天，true = 全部。</summary>
    public bool ShowAllFiles => _showAllFiles;

    /// <summary>当前列表里实际显示的文件数。</summary>
    public int VisibleFileCount => _files.Count;

    /// <summary>给诊断自检用的状态描述。</summary>
    public string FileFilterStateReport =>
        $"只看当天={FileFilterToday.IsChecked} 全部={FileFilterAll.IsChecked} 范围={(_showAllFiles ? "全部" : "当天")}";

    /// <summary>
    /// 和页签同一类问题：ToggleButton 之间不会互斥，而且再点一次当前胶囊
    /// 会把 IsChecked 反选成 false。所以只认「勾上的那一个」，
    /// 如果是把自己点掉了，就维持当前筛选，不允许两个都不选（那会变成两个条件都不成立）。
    /// </summary>
    private void OnFileFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        bool showAll = ReferenceEquals(clicked, FileFilterAll);
        SetFileFilter(clicked.IsChecked == true ? showAll : _showAllFiles);
    }

    /// <summary>
    /// 切换文件筛选。两个胶囊的勾选状态和列表内容全部由 showAll 这一个值推导，
    /// 不再互相读取对方的 IsChecked。
    /// </summary>
    public void SetFileFilter(bool showAll)
    {
        _showAllFiles = showAll;

        _syncing = true;
        FileFilterToday.IsChecked = !showAll;
        FileFilterAll.IsChecked = showAll;
        _syncing = false;

        RefreshFileList();
    }

    private void RefreshFileList()
    {
        var source = _showAllFiles
            ? App.Store.QueryFiles(null)
            : App.Store.QueryFiles(_currentDate.ToString("yyyy-MM-dd"));

        var list = FileSearch.Filter(source, _fileKeyword);

        // 「全部」视图要按归档日期分组，而分组的先后取决于记录在集合里的出现顺序，
        // 所以这里必须先把顺序排好（日期从新到旧，同一天内按拖入时刻从新到旧）。
        if (_showAllFiles) list = DateGrouping.Sort(list);

        _syncing = true;
        _files.Clear();
        foreach (var f in list) _files.Add(f);
        _syncing = false;

        ApplyFileGrouping();
        RefreshFileCalendarBar(list);

        FileEmptyHint.Text = _fileKeyword.Length > 0
            ? $"没有包含「{_fileKeyword}」的文件。换个词，或者把搜索框清空看全部。"
            : _showAllFiles
                ? "还没有归档文件。把文件拖到窗口里试试。"
                : $"{_currentDate:M月d日}还没有归档文件。把文件拖到窗口里，或切到「全部」看看历史。";
        FileEmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var byCategory = list
            .GroupBy(f => f.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()}")
            .ToList();

        var scope = _fileKeyword.Length > 0
            ? $"搜「{Truncate(_fileKeyword, 10)}」"
            : _showAllFiles ? FileScopeSummary(list) : $"{_currentDate:M月d日}";

        FileStatText.Text = list.Count == 0
            ? (_fileKeyword.Length > 0 ? "没有匹配" : "暂无文件")
            : $"{scope} {list.Count} 个 · " + string.Join(" · ", byCategory.Take(4));

        // 放在这里是因为所有"文件变了"或"日期变了"的路径最后都会走到 RefreshFileList，
        // 单一入口，不容易漏。
        RefreshDayFiles();
    }

    // ------------------------------------------------------------------
    // 归档页：搜索
    // ------------------------------------------------------------------

    /// <summary>当前搜索关键字（空串 = 不筛）。诊断自检用。</summary>
    public string FileKeyword => _fileKeyword;

    private void OnFileSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        FileSearchPlaceholder.Visibility = string.IsNullOrEmpty(FileSearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_syncingSearch) return;
        ApplyFileKeyword(FileSearchBox.Text);
    }

    /// <summary>
    /// 从头设置搜索关键字（会一并改搜索框的内容）。
    ///
    /// autoWiden：在「只看当天」里输入关键字时自动放宽到「全部」。
    /// 搜索的心智是"在归档里找那个文件"，而不是"在这一天里找" —— 不放宽的话，
    /// 用户敲完发现自己要找的东西明明归档过却没有，只会以为记录丢了。放宽会在状态栏说明。
    /// </summary>
    public void SetFileKeyword(string keyword, bool autoWiden = true)
    {
        _syncingSearch = true;
        FileSearchBox.Text = keyword ?? "";
        _syncingSearch = false;

        FileSearchPlaceholder.Visibility = string.IsNullOrEmpty(FileSearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyFileKeyword(keyword, autoWiden);
    }

    private void ApplyFileKeyword(string? keyword, bool autoWiden = true)
    {
        _fileKeyword = FileSearch.Normalize(keyword);

        bool widen = autoWiden && _fileKeyword.Length > 0 && !_showAllFiles;
        if (widen)
        {
            // 只改状态字段 + 两颗胶囊，不走 SetFileFilter —— 那条路会先刷一次列表，
            // 然后这里再刷第二次。同一个入口刷两遍不会错，只是白做一次全量重排。
            _showAllFiles = true;
            _syncing = true;
            FileFilterToday.IsChecked = false;
            FileFilterAll.IsChecked = true;
            _syncing = false;
        }

        RefreshFileList();
        if (widen) SetStatus("已在「全部文件」范围内搜索");
    }

    // ------------------------------------------------------------------
    // 归档页：归档日历
    // ------------------------------------------------------------------

    /// <summary>归档日历那一行是否露出。诊断自检用。</summary>
    public bool FileCalendarVisible => FileCalendarBar.Visibility == Visibility.Visible;

    /// <summary>归档日历面板（自检要读它标了哪些日子、格子上写了什么数字）。</summary>
    public MonthCalendarPanel FileCalendar => FileCalendarPanel;

    /// <summary>日历按钮上跟着的那个日期。诊断自检用。</summary>
    public DateTime FileCalendarAnchor => _fileCalendarAnchor;

    /// <summary>「未标注日期」胶囊兜住的文件数（0 = 没有这种文件）。诊断自检用。</summary>
    public int FileUndatedCount =>
        int.TryParse(FileUndatedButton.Tag as string, out var n) ? n : 0;

    /// <summary>
    /// 「未标注日期」那颗胶囊现在露不露。诊断自检用。
    ///
    /// 必须和 FileUndatedCount 分开断言：那个数是从 Tag 读的，**不显示也会被赋值**，
    /// 只看数字的话"把胶囊藏起来"这种改动照样能过（自检反验时确实漏过一次）。
    /// </summary>
    public bool FileUndatedVisible => FileUndatedButton.Visibility == Visibility.Visible;

    private DateTime _fileCalendarAnchor = DateTime.Today;

    /// <summary>
    /// 刷新归档日历那一行：要不要露出来、锚在哪天、有没有"未标注日期"要兜住。
    ///
    /// 日历标的日子**跟着当前这份列表走**（也就是跟着搜索词走），不是整库 ——
    /// 否则会出现"搜出 3 个文件、日历上却有 12 个亮点"，点一个没命中的日子直接跳到空处。
    /// 「格子上的数字 = 这份列表里那天有几个」，说得清、也一定点得动。
    /// </summary>
    private void RefreshFileCalendarBar(IReadOnlyList<FileRecord> list)
    {
        var counts = DayIndex.ArchiveCounts(list);

        // 锚定日期：默认落在"最新一个归档日"，用户点过某天之后就跟着那一天走。
        // 但它要是已经不在这份列表里了（文件被移除、或搜索把它筛掉了），就退回默认 ——
        // 免得按钮上顶着一个这份列表里根本没有的日期。
        if (!counts.ContainsKey(_fileCalendarAnchor.ToString("yyyy-MM-dd")))
            _fileCalendarAnchor = NewestArchiveDay(counts);

        // 面板始终跟着当前这份列表刷新（**即使这一行是收起的**）：
        // 收起时反正没人看，代价只是 42 个格子；换来的是"面板里的数字永远等于列表里的内容"，
        // 自检可以直接读它，不用先想办法把面板打开。
        FileCalendarPanel.Show(_fileCalendarAnchor, _fileCalendarAnchor, null, counts);

        // 只有一天有归档就不摆日历：42 格里只有一个点，而它指的那一组就在屏幕上，
        // "点一下滚到那里"没有任何意义（和改版前那排目录胶囊是同一条规矩）。
        bool show = _showAllFiles && counts.Count > 1;
        FileCalendarBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // 没标日期的文件在日历上**没有落点**（键就是日期本身），改版前它是一颗
        // 「未标注日期」的胶囊。得留个入口，否则这几个人会从界面上彻底消失。
        int undated = list.Count(f => string.IsNullOrEmpty(f.AddedDate));
        FileUndatedButton.Tag = undated.ToString();
        FileUndatedButton.Visibility = undated > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (undated > 0)
        {
            var label = new TextBlock { Text = $"未标注日期 {undated}", FontSize = 11.5 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            FileUndatedButton.Content = label;
        }

        FileCalendarAnchorText.Text =
            DayIndex.DateLabel(_fileCalendarAnchor.ToString("yyyy-MM-dd"), DateTime.Today);
    }

    /// <summary>
    /// 最新的一天归档。一个都没有就给今天（按钮上总要有个日期，空着更费解）。
    /// 键是定长零填充的 yyyy-MM-dd，所以字符串最大就是时间最新，不用解析成 DateTime 再比。
    /// </summary>
    private static DateTime NewestArchiveDay(IReadOnlyDictionary<string, int> counts)
    {
        var newest = counts.Keys
            .Where(k => k.Length > 0)
            .OrderByDescending(k => k, StringComparer.Ordinal)
            .FirstOrDefault();

        return newest != null && DateTime.TryParse(newest, out var d) ? d.Date : DateTime.Today;
    }

    private DateTime _fileCalendarClosedAt = DateTime.MinValue;

    private void OnFileCalendarButtonClick(object sender, RoutedEventArgs e)
    {
        if (FileCalendarPopup.IsOpen)
        {
            FileCalendarPopup.IsOpen = false;
            return;
        }

        // 和月历 / 往期清单同一套：StaysOpen=False 的弹出层被自己的按钮点到时会先自关，
        // 那一次点击不会再走到这里，所以加个时间窗，避免"刚关掉又被立刻打开"。
        if ((DateTime.UtcNow - _fileCalendarClosedAt).TotalMilliseconds < 260) return;

        OpenFileCalendar();
    }

    /// <summary>把归档计数灌进日历并打开它。诊断自检走的是同一条路。</summary>
    public void OpenFileCalendar()
    {
        var counts = DayIndex.ArchiveCounts(_files);

        // selected 传锚定日：打开就停在上次看的那个月，而不是每次都从今天那个月翻起。
        FileCalendarPanel.Show(_fileCalendarAnchor, _fileCalendarAnchor, null, counts);
        FileCalendarPopup.IsOpen = true;
        FileCalendarPanel.FocusJumpInput();
    }

    private void OnFileCalendarPopupClosed(object? sender, EventArgs e)
        => _fileCalendarClosedAt = DateTime.UtcNow;

    /// <summary>
    /// 在归档日历里点了某一天：收起日历，把文件列表滚到那一组。
    ///
    /// 不做"自动切到全部 / 自动清搜索"那类静默改状态的事 ——
    /// 日历标的日子本来就是从当前列表算出来的，所以每一颗亮点都必然点得动。
    /// </summary>
    private void OnFileCalendarPanelDateChosen(DateTime date)
    {
        FileCalendarPopup.IsOpen = false;
        _fileCalendarAnchor = date.Date;
        FileCalendarAnchorText.Text = DayIndex.DateLabel(date.ToString("yyyy-MM-dd"), DateTime.Today);

        var key = date.ToString("yyyy-MM-dd");
        int count = DayIndex.ArchiveCounts(_files).TryGetValue(key, out var n) ? n : 0;
        var label = DayIndex.DateLabel(key, DateTime.Today);

        SetStatus(JumpToFileGroup(key)
            ? $"已滚到 {label} · {count} 个文件"
            : $"{label} 这一组不在当前列表里（先清掉搜索词试试）");
    }

    /// <summary>「未标注日期」那颗胶囊：同样是滚到对应分组。</summary>
    private void OnFileUndatedClick(object sender, RoutedEventArgs e)
        => SetStatus(JumpToFileGroup(DateGrouping.UnknownKey)
            ? "已滚到「未标注日期」那一组"
            : "「未标注日期」这一组不在当前列表里");

    /// <summary>
    /// 把列表滚到某个日期的分组。返回 false 表示这个日期不在当前列表里 ——
    /// 界面据此不假装"跳过了"，诊断也靠它验"日历上的日期和列表里的分组是一回事"。
    /// </summary>
    public bool JumpToFileGroup(string dateKey)
    {
        var group = CollectionViewSource.GetDefaultView(_files).Groups?
            .OfType<CollectionViewGroup>()
            .FirstOrDefault(g => (g.Name as string ?? DateGrouping.UnknownKey) == dateKey);
        if (group == null) return false;

        // 分组容器是布局时才生成的。没生成就先把列表走一遍布局再取，
        // 免得"点目录没反应"看起来像跳转逻辑坏了。
        var container = FileList.ItemContainerGenerator.ContainerFromItem(group) as FrameworkElement;
        if (container == null)
        {
            FileList.UpdateLayout();
            container = FileList.ItemContainerGenerator.ContainerFromItem(group) as FrameworkElement;
        }
        if (container == null) return false;

        container.BringIntoView();
        return true;
    }

    /// <summary>今日记录页里的「去归档 ›」：直接切到文件归档页。</summary>
    private void OnGoToFilesPageClick(object sender, RoutedEventArgs e) => GoToFilesOfCurrentDay();

    /// <summary>
    /// 跳到文件归档页看这一天的文件。
    ///
    /// 顺手把范围收回「只看当天」：这个动作的语义是"看这一天的全部文件"，
    /// 上一次留在「全部」的话，跳过去看到的是一整片历史，还得自己再筛一次。
    /// public 是为了自检 —— 按钮没名字，探针点不到它。
    /// </summary>
    public void GoToFilesOfCurrentDay()
    {
        SetFileFilter(false);
        SwitchTo(NavPage.Files);
    }

    /// <summary>
    /// 刷新今日记录页里那份「当天归档的文件」。
    ///
    /// 只看当前查看的日期，不受文件归档页筛选的影响；按拖入时刻从新到旧排，
    /// 和文件归档页的顺序口径一致。
    /// </summary>
    private void RefreshDayFiles()
    {
        var list = App.Store.FilesOnDate(_currentDate)
            .OrderByDescending(f => f.AddedAt)
            .ToList();

        _dayFiles.Clear();
        foreach (var f in list) _dayFiles.Add(f);

        bool isToday = _currentDate.Date == DateTime.Today;
        DayFilesTitle.Text = isToday ? "今日归档的文件" : $"{_currentDate:M月d日}归档的文件";
        DayFilesCountText.Text = $"{list.Count} 个";

        // 一个文件都没有时整段收起来：外面的空提示已经说了"把文件拖进来"，
        // 再挂一个 0 个文件的标题只是噪音。
        DayFilesSection.Visibility = list.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>「全部」视图的统计口径里带上跨了几天，跟日期分组对应。</summary>
    private static string FileScopeSummary(List<FileRecord> list)
    {
        int days = DateGrouping.CountGroups(list);
        return days > 1 ? $"共 {days} 天" : "共";
    }

    /// <summary>
    /// 是否给文件列表加日期分组。
    ///
    /// 只有「全部」视图需要：它可能跨很多天。而「只看当天」里所有记录本来就是同一天，
    /// 再加一个栏头只是噪音。分组属性挂在默认视图上，ItemsControl 用的就是它。
    /// </summary>
    private void ApplyFileGrouping()
    {
        var view = CollectionViewSource.GetDefaultView(_files);

        using (view.DeferRefresh())
        {
            view.GroupDescriptions.Clear();
            if (_showAllFiles)
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileRecord.AddedDate)));
        }
    }

    /// <summary>当前列表的日期分组数（0 表示没分组）。诊断自检用。</summary>
    public int FileGroupCount => FileGroups.Count;

    /// <summary>
    /// 取消尚未落盘的节流保存。诊断自检用。
    ///
    /// 探针模拟拖动时会触发正常的保存节流，如果那次定时器在自检结束后才走到，
    /// 就会把探针的值写进用户配置 —— 所以自检收尾时先把它停掉。
    /// </summary>
    public void CancelPendingSaves()
    {
        _debounce.Stop();
        _pendingAction = null;
    }

    /// <summary>
    /// `_syncing` 守卫是否还按着。
    ///
    /// 它被卡在 true 的话，所有"界面改动 → 保存设置"的处理器都会静默 return：
    /// 控件本身照样动（比如滑块能拖），但文本不更新、设置不落盘，表现就是"调了没反应"。
    /// 诊断自检用它做不变量断言。
    /// </summary>
    public bool IsSyncingGuardSet => _syncing;

    /// <summary>当前分组（分组键 + 条数），按界面上的先后顺序。诊断自检用。</summary>
    public IReadOnlyList<(string Key, int Count)> FileGroups =>
        CollectionViewSource.GetDefaultView(_files).Groups?
            .OfType<CollectionViewGroup>()
            .Select(g => (g.Name as string ?? DateGrouping.UnknownKey, g.ItemCount))
            .ToList()
        ?? new List<(string, int)>();

    /// <summary>分组结构摘要，形如「今天 · 9月11日 2 | 周一 · 9月7日 1」。诊断自检用。</summary>
    public string FileGroupReport => FileGroups.Count == 0
        ? "（未分组）"
        : string.Join(" | ", FileGroups.Select(g => $"{DateGrouping.Label(g.Key)} {g.Count}"));

    /// <summary>今日记录页里那段「归档的文件」是否显示。诊断自检用。</summary>
    public bool DayFilesSectionVisible => DayFilesSection.Visibility == Visibility.Visible;

    /// <summary>今日记录页里文件卡片的条数。诊断自检用。</summary>
    public int DayFileCardCount => _dayFiles.Count;

    /// <summary>
    /// 今日记录页里每张文件卡片在窗口坐标里的矩形，按显示顺序。
    /// 诊断自检用它验证「多条堆叠时不会互相压住」。
    ///
    /// 量的是卡片本身（模板根节点那个 Border），不是 ItemsControl 的容器 ——
    /// 卡片自带的下外边距落在容器内部，量容器的话相邻两张永远严丝合缝地贴着，
    /// 看不出"是不是真的留了间距"。
    /// </summary>
    public IReadOnlyList<Rect> DayFileCardBounds
    {
        get
        {
            var bounds = new List<Rect>();

            for (int i = 0; i < _dayFiles.Count; i++)
            {
                if (DayFileList.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container)
                {
                    // 还没生成容器（窗口没走布局）就直接放弃，免得报出误导性的结论
                    continue;
                }

                var card = System.Windows.Media.VisualTreeHelper.GetChildrenCount(container) > 0
                    ? System.Windows.Media.VisualTreeHelper.GetChild(container, 0) as FrameworkElement
                    : null;

                if (card == null) continue;

                var origin = card.TranslatePoint(new Point(0, 0), this);
                bounds.Add(new Rect(origin, new Size(card.ActualWidth, card.ActualHeight)));
            }

            return bounds;
        }
    }

    /// <summary>文件归档页用的卡片模板。诊断自检用，用来比对两处样式是否同源。</summary>
    public object? ArchiveFileTemplate => FileList.ItemTemplate;

    /// <summary>今日记录页用的卡片模板。诊断自检用。</summary>
    public object? NotesFileTemplate => DayFileList.ItemTemplate;

    private void OnRevealFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not FileRecord rec) return;

        try
        {
            if (!File.Exists(rec.FullPath))
            {
                rec.IsMissing = true;
                MessageBox.Show(this,
                    $"文件已经不在原位置了：\n{rec.FullPath}\n\n记录仍然保留，方便你回头查它当时叫什么、在哪儿。",
                    "文件未找到", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{rec.FullPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus($"打开失败：{ex.Message}");
        }
    }

    private void OnShowVersionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not FileRecord rec) return;

        var chain = App.Store.VersionChain(rec.VersionGroupKey);
        if (chain.Count == 0)
        {
            chain = new List<FileRecord> { rec };
        }

        var sb = new StringBuilder();
        sb.AppendLine($"版本链：{VersionService.GroupKeyDisplay(rec.VersionGroupKey)}");
        sb.AppendLine();

        foreach (var item in chain.OrderBy(x => x.Version))
        {
            var marker = item.Id == rec.Id ? "  ← 当前" : "";
            sb.AppendLine($"v{item.Version}   {item.LastWriteTime:yyyy-MM-dd HH:mm}   {FileRecord.FormatSize(item.SizeBytes)}");
            sb.AppendLine($"      {ShortHash(item.Sha256)}{marker}");
            sb.AppendLine($"      {item.FullPath}");
            if (!string.IsNullOrEmpty(item.SnapshotPath))
                sb.AppendLine($"      快照：{item.SnapshotPath}");
            sb.AppendLine();
        }

        if (chain.Count == 1)
            sb.AppendLine("目前只有这一个版本。修改后再次拖进来，就会自动标记为 v2。");

        MessageBox.Show(this, sb.ToString(), $"版本记录 · {rec.FileName}",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string ShortHash(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return "（无指纹）";
        var sampled = hash.StartsWith("sample:", StringComparison.Ordinal);
        var body = sampled ? hash["sample:".Length..] : hash;
        var prefix = sampled ? "采样指纹 " : "SHA-256 ";
        return prefix + (body.Length > 16 ? body[..16] + "…" : body);
    }

    private void OnRemoveFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not FileRecord rec) return;

        var confirm = MessageBox.Show(this,
            $"从记录中移除「{rec.FileName}」？\n\n只是删掉这条归档记录，磁盘上的文件不会被删除或修改。",
            "移除记录", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        App.Store.Book.Files.Remove(rec);
        App.Store.Save();
        RefreshFileList();
        RefreshStats();
        SetStatus("已从归档中移除");
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e) => App.OpenDataFolder();

    private void OnSaveNowClick(object sender, RoutedEventArgs e)
    {
        App.Store.Save();
        App.Settings.Save();
        SetStatus($"已保存到 {AppPaths.Root}");
    }

    private void OnResetRulesClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "把分类规则恢复成内置默认值？自定义的类别和关键词会丢失。",
            "重置分类规则", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        App.Classify.ResetToDefaults();
        SetStatus("分类规则已恢复默认");
    }

    // ==================================================================
    // 设置
    // ==================================================================

    private void LoadSettingsToUi()
    {
        var s = App.Settings.Settings;

        _syncing = true;
        WorkStartBox.Text = s.WorkStartHour.ToString();
        WorkEndBox.Text = s.WorkEndHour.ToString();
        RemindBeforeBox.Text = s.RemindMinutesBefore.ToString();
        NudgeBox.Text = s.OvertimeNudgeMinutes.ToString();
        HashLimitBox.Text = s.FullHashLimitMb.ToString();

        ReminderEnabledBox.IsChecked = s.ReminderEnabled;
        ReminderSoundBox.IsChecked = s.ReminderSoundEnabled;
        ReminderBalloonBox.IsChecked = s.ReminderTrayBalloonEnabled;

        FloatingEnabledBox.IsChecked = s.FloatingEnabled;
        FloatingSnapBox.IsChecked = s.FloatingSnapToEdge;
        FloatingCollapseBox.IsChecked = s.FloatingAutoCollapse;
        FloatingDropOpenBox.IsChecked = s.AutoOpenMainWindowOnDrop;

        ScaleSlider.Value = s.FloatingScale;
        OpacitySlider.Value = s.FloatingOpacity;
        ScaleText.Text = $"{s.FloatingScale:0.00}×";
        OpacityText.Text = $"{s.FloatingOpacity * 100:0}%";

        SnapshotBox.IsChecked = s.SnapshotMode;
        StartupBox.IsChecked = s.StartWithWindows;
        HideOnCloseBox.IsChecked = s.HideMainWindowOnClose;

        ThemeSystem.IsChecked = s.ThemeOverride == AppThemeMode.System;
        ThemeLight.IsChecked = s.ThemeOverride == AppThemeMode.Light;
        ThemeDark.IsChecked = s.ThemeOverride == AppThemeMode.Dark;

        HeaderSubtitle.Text = $"· {s.WorkEndHour:00}:{s.WorkEndMinute:00} 下班";
        QuickCaptureEnabledBox.IsChecked = s.QuickCaptureEnabled;
        HotKeyText.Text = string.IsNullOrWhiteSpace(s.QuickCaptureHotkey) ? "未设置" : s.QuickCaptureHotkey;
        RefreshCharacterChips();
        _syncing = false;

        RefreshHotKeyHint();
        RefreshShortcutHint();
    }

    // ==================================================================
    // 形象切换（桌面小鲸鱼）
    // ==================================================================

    /// <summary>
    /// 按磁盘上实际能找到的形象包，重建那一排形象按钮。
    ///
    /// 动态生成而不是在 XAML 里写死两个按钮：形象包是"往 characters\ 里丢一个目录"
    /// 就能多出来的东西（用户也可以自己做），写死的话每加一套形象都得改代码。
    /// </summary>
    private void RefreshCharacterChips()
    {
        if (CharacterChips == null) return;

        CharacterChips.Children.Clear();

        var wanted = App.Settings.Settings.FloatingCharacter ?? "";

        // 展示的是**实际生效**的那个（可能已经把设置里的值降级了），
        // 否则会出现"选了 A、界面显示 A 高亮、实际跑的是 B"这种最难查的不一致。
        var effective = CharacterPacks.Resolve(wanted);
        var packs = CharacterPacks.Available();

        foreach (var pack in packs)
        {
            var chip = new RadioButton
            {
                Content = pack.IsAvailable ? pack.Title : pack.Title + "（缺素材）",
                GroupName = "FloatingCharacter",
                Tag = pack.Id,
                IsEnabled = pack.IsAvailable,
                IsChecked = string.Equals(pack.Id, effective, StringComparison.OrdinalIgnoreCase),
                ToolTip = pack.IsAvailable
                    ? $"{pack.Note}{Environment.NewLine}{pack.Root}"
                    : $"没有找到素材目录：{pack.Root}{Environment.NewLine}放好素材后重启程序即可使用。",
                VerticalAlignment = VerticalAlignment.Center,
            };

            // 直接借导航按钮那一套圆角胶囊样式（选中会高亮成主题色）
            try { chip.Style = (Style)FindResource("ChipToggle"); } catch { /* 取不到就用默认样式 */ }

            chip.Checked += OnCharacterChipChecked;
            CharacterChips.Children.Add(chip);
        }

        CharacterHint.Text = DescribeCharacterChoice(wanted, effective, packs);
    }

    private void OnCharacterChipChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (sender is not RadioButton chip || chip.Tag is not string id || id.Length == 0) return;

        App.Settings.Update(s => s.FloatingCharacter = id);
        CharacterHint.Text = DescribeCharacterChoice(id, CharacterPacks.Resolve(id),
                                                     CharacterPacks.Available());
    }

    /// <summary>
    /// 形象选择下面那几行说明。
    ///
    /// 两件必须说出来的事：
    ///
    /// ① 设置里选的和实际生效的不一样。用户把素材目录删了之后界面会静默回落，
    ///    没有这句话他只会觉得"这个开关坏了"。
    /// ② **哪套形象缺素材、缺在哪个目录**。缺素材时按钮是禁用的，点上去毫无反应，
    ///    而"为什么点不动"如果只写在 ToolTip 里就等于没写 —— 没人会去悬停一个灰按钮。
    ///    2026-09-20 用户就是这么被卡住的：他在开发目录里跑，那个目录旁边没有
    ///    <c>Assets\characters\</c>，于是「溟月」是灰的，看起来就是"点了没反应"。
    /// </summary>
    private static string DescribeCharacterChoice(string wanted, string effective,
                                                 IReadOnlyList<CharacterPacks.Pack> packs)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(wanted) &&
            !wanted.Equals(effective, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"设置里选的是「{wanted}」，但它的素材不在，已经自动改用" +
                      $"「{CharacterPacks.TitleOf(effective)}」。");
        }

        var missing = packs.Where(p => !p.IsAvailable).ToList();
        foreach (var pack in missing)
        {
            lines.Add($"「{pack.Title}」缺素材，上面那个按钮是灰的、点不动 —— " +
                      $"把形象包放进 {pack.Root}，重启程序就能选。");
        }

        lines.Add(CharacterPacks.NoteOf(effective));
        lines.Add(missing.Count == 0
            ? "改完立刻生效，不用重启。"
            : "在现有的几套形象之间切换是立刻生效的；新放进来的素材要重启程序才会被扫到。");

        return string.Join(Environment.NewLine, lines);
    }

    // ------------------------------------------------------------------
    // 诊断自检用的钩子（设置页那一排形象按钮）
    //
    // 为什么要给这几个按钮开钩子：形象切换是**纯界面**的事。解析函数全对，
    // 但按钮被禁用时用户看到的仍然是"点新形象没反应" —— 2026-09-20 的真实反馈。
    // 断言"这个按钮能不能点"比断言"解析函数返回了什么"更贴近用户碰到的那一层。
    // ------------------------------------------------------------------

    /// <summary>设置页上形象按钮的个数。诊断自检用。</summary>
    public int CharacterChipCount => CharacterChips?.Children.Count ?? 0;

    /// <summary>那排按钮里的 radio（空面板时给个空序列，省得每处都判空）。</summary>
    private IEnumerable<RadioButton> CharacterChipButtons
        => CharacterChips == null
            ? Enumerable.Empty<RadioButton>()
            : CharacterChips.Children.OfType<RadioButton>();

    /// <summary>某个形象按钮现在的样子：在不在、能不能点、选没选中、按钮上写的什么。诊断自检用。</summary>
    public (bool Found, bool Enabled, bool Checked, string Text) CharacterChipState(string id)
    {
        foreach (var chip in CharacterChipButtons)
        {
            if (chip.Tag is string tag && tag.Equals(id, StringComparison.OrdinalIgnoreCase))
                return (true, chip.IsEnabled, chip.IsChecked == true, chip.Content as string ?? "");
        }

        return (false, false, false, "");
    }

    /// <summary>形象选择下面那几行说明的字。诊断自检用。</summary>
    public string CharacterHintText => CharacterHint?.Text ?? "";

    /// <summary>
    /// 真按一下那个按钮（翻 IsChecked，Checked 事件照常抛出来）。
    ///
    /// 刻意不直接改设置：要验的是"这一下点下去到底有没有用"。
    /// 按钮禁用时返回 false —— 那正是"点了没反应"。
    /// 诊断自检用。
    /// </summary>
    public bool ClickCharacterChip(string id)
    {
        foreach (var chip in CharacterChipButtons)
        {
            if (chip.Tag is string tag && tag.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                if (!chip.IsEnabled) return false;
                chip.IsChecked = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>按当前素材目录重建形象按钮（正常是切到设置页时自动跑）。诊断自检用。</summary>
    public void ReloadCharacterChips() => RefreshCharacterChips();

    private void OnWorkTimeChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        Debounce(() =>
        {
            App.Settings.Update(s =>
            {
                if (int.TryParse(WorkStartBox.Text.Trim(), out var sh)) s.WorkStartHour = sh;
                if (int.TryParse(WorkEndBox.Text.Trim(), out var eh)) s.WorkEndHour = eh;
            });
            LoadSettingsToUi();
        });
    }

    private void OnRemindBeforeChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        Debounce(() =>
        {
            if (int.TryParse(RemindBeforeBox.Text.Trim(), out var m))
                App.Settings.Update(s => s.RemindMinutesBefore = m);
        });
    }

    private void OnNudgeChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        Debounce(() =>
        {
            if (int.TryParse(NudgeBox.Text.Trim(), out var m))
                App.Settings.Update(s => s.OvertimeNudgeMinutes = m);
        });
    }

    private void OnHashLimitChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        Debounce(() =>
        {
            if (int.TryParse(HashLimitBox.Text.Trim(), out var m))
                App.Settings.Update(s => s.FullHashLimitMb = m);
        });
    }

    private void OnReminderToggle(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.Settings.Update(s =>
        {
            s.ReminderEnabled = ReminderEnabledBox.IsChecked == true;
            s.ReminderSoundEnabled = ReminderSoundBox.IsChecked == true;
            s.ReminderTrayBalloonEnabled = ReminderBalloonBox.IsChecked == true;
            if (s.ReminderEnabled) s.SnoozeUntil = null;
        });
        RefreshStats();
    }

    private void OnFloatingToggle(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        App.Settings.Update(s =>
        {
            s.FloatingEnabled = FloatingEnabledBox.IsChecked == true;
            s.FloatingSnapToEdge = FloatingSnapBox.IsChecked == true;
            s.FloatingAutoCollapse = FloatingCollapseBox.IsChecked == true;
            s.AutoOpenMainWindowOnDrop = FloatingDropOpenBox.IsChecked == true;
        });

        var app = (App)Application.Current;
        if (App.Settings.Settings.FloatingEnabled) app.ShowFloating();
        else app.HideFloating();
    }

    private void OnFloatingSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        // 双保险：即便将来有人把订阅挪回 XAML，也不会因为控件未构造而崩
        if (ScaleText == null || OpacityText == null) return;

        ScaleText.Text = $"{ScaleSlider.Value:0.00}×";
        OpacityText.Text = $"{OpacitySlider.Value * 100:0}%";

        var scale = ScaleSlider.Value;
        var opacity = OpacitySlider.Value;

        // 先让悬浮窗立刻跟上：落盘是 700ms 节流，如果连画面也等它，
        // 拖动全程鲸鱼都不动，看起来就是"这个设置调不了"。
        App.Instance.Floating?.PreviewScaleAndOpacity(scale, opacity);

        Debounce(() => App.Settings.Update(s =>
        {
            s.FloatingScale = scale;
            s.FloatingOpacity = opacity;
        }));
    }

    private void OnSnapshotToggle(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        if (SnapshotBox.IsChecked == true)
        {
            var confirm = MessageBox.Show(this,
                "开启后，每个新版本都会在数据目录的 archive 下复制一份副本。\n\n" +
                "好处：版本真的留下来了，可以回滚。\n" +
                "代价：占用磁盘空间，需要你自己定期清理。\n\n原文件仍然不会被改动。继续吗？",
                "开启归档快照", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (confirm != MessageBoxResult.OK)
            {
                _syncing = true;
                SnapshotBox.IsChecked = false;
                _syncing = false;
                return;
            }
        }

        App.Settings.Update(s => s.SnapshotMode = SnapshotBox.IsChecked == true);
        SetStatus(SnapshotBox.IsChecked == true ? "已开启归档快照" : "已关闭归档快照");
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        var mode = sender == ThemeLight ? AppThemeMode.Light
                 : sender == ThemeDark ? AppThemeMode.Dark
                 : AppThemeMode.System;

        App.Settings.Update(s => s.ThemeOverride = mode);

        _syncing = true;
        ThemeSystem.IsChecked = mode == AppThemeMode.System;
        ThemeLight.IsChecked = mode == AppThemeMode.Light;
        ThemeDark.IsChecked = mode == AppThemeMode.Dark;
        _syncing = false;

        App.Theme.ForceApply();
    }

    private void OnStartupToggle(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        bool want = StartupBox.IsChecked == true;
        bool ok = App.AutoStart.Apply(want);

        if (!ok && want)
        {
            _syncing = true;
            StartupBox.IsChecked = false;
            _syncing = false;
            MessageBox.Show(this, "写入开机启动项失败，可能被安全软件拦截了。",
                "开机自启", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        App.Settings.Update(s => s.StartWithWindows = want);
        SetStatus(want ? "已设置开机自启" : "已取消开机自启");
    }

    private void OnHideOnCloseToggle(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.Settings.Update(s => s.HideMainWindowOnClose = HideOnCloseBox.IsChecked == true);
    }

    // ==================================================================
    // 日报 / 周报
    // ==================================================================

    private void OnDailyReportClick(object sender, RoutedEventArgs e)
    {
        // 先把小结存下来，日报里要带上它
        FlushSummary();
        ((App)Application.Current).ShowDailyReport(_currentDate);
        SetStatus($"已生成 {_currentDate:yyyy-MM-dd} 的日报");
    }

    private void OnWeeklyReportClick(object sender, RoutedEventArgs e)
    {
        FlushSummary();
        ((App)Application.Current).ShowWeeklyReport(_currentDate);
        SetStatus($"已生成 {ReportService.WeekStart(_currentDate):yyyy-MM-dd} 起这一周的周报");
    }

    private void OnCopySummaryClick(object sender, RoutedEventArgs e)
    {
        var text = SummaryInput.Text.Trim();
        if (text.Length == 0)
        {
            SetStatus("今日小结还是空的");
            return;
        }

        try
        {
            Clipboard.SetText(text);
            SetStatus("小结已复制到剪贴板");
        }
        catch
        {
            SetStatus("复制失败，剪贴板可能被其他程序占用");
        }
    }

    /// <summary>立刻把小结写盘，不等待防抖计时器。</summary>
    private void FlushSummary()
    {
        _debounce.Stop();
        var pending = _pendingAction;
        _pendingAction = null;
        try { pending?.Invoke(); } catch { /* 落盘失败不影响生成报告 */ }

        var text = SummaryInput.Text;
        App.Store.GetOrCreateDay(_currentDate).Summary = text;
        App.Store.Save();
    }

    // ==================================================================
    // 快速记录 / 快捷方式
    // ==================================================================

    private bool _recordingHotKey;

    private void OnQuickCaptureToggle(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        App.Settings.Update(s => s.QuickCaptureEnabled = QuickCaptureEnabledBox.IsChecked == true);
        App.Instance.ApplyQuickCaptureHotKey();
        RefreshHotKeyHint();
    }

    private void OnRecordHotKeyClick(object sender, RoutedEventArgs e)
    {
        if (_recordingHotKey)
        {
            StopRecordingHotKey();
            return;
        }

        _recordingHotKey = true;
        RecordHotKeyButton.Content = "按下组合键…";
        HotKeyHint.Text = "现在按下想要的组合键，Esc 取消";
        PreviewKeyDown += OnRecordingKeyDown;
    }

    private void OnRecordingKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotKey) return;
        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            StopRecordingHotKey();
            return;
        }

        // Alt 组合键在 WPF 里是 Key.System，真正的键在 SystemKey 上
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var combo = HotKeyService.FromKeyEvent(key, Keyboard.Modifiers);

        if (combo == null)
        {
            HotKeyHint.Text = "组合键里至少要有一个 Ctrl / Alt / Shift / Win";
            return;
        }

        App.Settings.Update(s => s.QuickCaptureHotkey = combo);
        App.Instance.ApplyQuickCaptureHotKey();

        StopRecordingHotKey();
        LoadSettingsToUi();
        SetStatus($"快速记录快捷键已改为 {combo}");
    }

    private void StopRecordingHotKey()
    {
        _recordingHotKey = false;
        PreviewKeyDown -= OnRecordingKeyDown;
        RecordHotKeyButton.Content = "录制快捷键";
        RefreshHotKeyHint();
    }

    private void OnResetHotKeyClick(object sender, RoutedEventArgs e)
    {
        App.Settings.Update(s => s.QuickCaptureHotkey = "Ctrl+Alt+N");
        App.Instance.ApplyQuickCaptureHotKey();
        LoadSettingsToUi();
        SetStatus("快速记录快捷键已恢复为 Ctrl+Alt+N");
    }

    private void RefreshHotKeyHint()
    {
        var s = App.Settings.Settings;
        HotKeyHint.Text = !s.QuickCaptureEnabled
            ? "已关闭"
            : string.IsNullOrEmpty(App.Instance.HotKeyError)
                ? "全局生效，在别的程序里也能直接按"
                : App.Instance.HotKeyError;
    }

    private void OnCreateShortcutClick(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        if (app.CreateDesktopShortcut())
        {
            SetStatus($"桌面快捷方式已创建：{ShortcutService.ShortcutPath}");
        }

        RefreshShortcutHint();
    }

    private void RefreshShortcutHint()
    {
        ShortcutHint.Text = ShortcutService.Exists()
            ? "桌面上已有快捷方式，再点会按当前程序位置更新"
            : "在桌面创建一个带鲸鱼图标的快捷方式";
    }

    // ==================================================================
    // 拖拽归档
    // ==================================================================

    private static bool HasFiles(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop);

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!HasFiles(e.Data)) return;
        e.Effects = DragDropEffects.Copy;
        DropOverlay.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!HasFiles(e.Data)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!HasFiles(e.Data)) return;

        var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (paths == null || paths.Length == 0) return;

        e.Handled = true;
        await IntakeAsync(paths);
    }

    /// <summary>
    /// 共享的归档流程：主面板自己拖入、悬浮窗拖入都走这里。
    /// 记入的是 <see cref="_currentDate"/>（悬浮窗那条路会先把它拨到今天，见
    /// <see cref="IntakeDroppedAsync"/>）。返回逐条结果供调用方拼提示，出错返回 null。
    /// </summary>
    public async Task<IReadOnlyList<IntakeResult>?> IntakeAsync(string[] paths)
    {
        SetStatus($"正在归档 {paths.Length} 项…");
        AddEntryButton.IsEnabled = false;

        try
        {
            var results = await App.Intake.IntakeAsync(paths, _currentDate);

            int added = results.Count(r => r.Status == IntakeStatus.Added);
            int versioned = results.Count(r => r.Status == IntakeStatus.NewVersion);

            var scope = _currentDate.Date == DateTime.Today ? "" : $"（记入 {_currentDate:M月d日}）";
            var summary = FileIntakeService.Summarize(results);
            SetStatus(summary == "没有可归档的文件" ? summary : $"归档完成{scope}：{summary}");

            // 归档完还停在「今日记录」页时加一句指路：新加的这些文件在页面下方的清单里，
            // 而「文件归档」页能按类别看全部。已经在文件页就不用重复提示了。
            if (_activePage == NavPage.Notes && (added > 0 || versioned > 0))
                SetStatus(StatusText.Text + "（可在「文件归档」页查看）");

            RefreshFileList();
            RefreshStats();

            // 在今日记录页拖入文件时，文件清单接在事项后面，很可能落在可视区之外 ——
            // 自动滚过去，不然刚拖完什么反应都看不到。
            if (_activePage == NavPage.Notes && (added > 0 || versioned > 0) && _dayFiles.Count > 0)
            {
                DayFilesSection.BringIntoView();
            }

            return results;
        }
        catch (Exception ex)
        {
            SetStatus($"归档出错：{ex.Message}");
            return null;
        }
        finally
        {
            AddEntryButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 悬浮窗拖入文件的落点：**一律记入今天**，并按设置切到「文件归档」页。
    /// 返回给悬浮窗做气泡提示的一句话，外加"真收下了几个文件"
    /// —— 后者决定悬浮窗要不要演一段庆祝（重复拖入不该蹦起来）。
    ///
    /// 为什么不像主面板那样跟着"正在看的那一天"走：那条规则靠拖拽横幅上的字来说明
    /// （见 <see cref="UpdateDropBanner"/>），而悬浮窗上没有地方写这句话 ——
    /// 规则必须不言自明。把文件丢给桌面上的小鲸鱼，意思就是"此刻"；
    /// 想补录过去某天，用主面板自己拖。
    /// </summary>
    public async Task<FileIntakeService.IntakeOutcome> IntakeDroppedAsync(string[] paths)
    {
        if (paths.Length == 0) return new("没有可归档的文件", 0);

        // 先把这个窗口拨到今天再归档：IntakeAsync 记的是 _currentDate，
        // 顺序反了就会记到用户上次翻到的那一天去。
        if (_currentDate.Date != DateTime.Today) LoadDay(DateTime.Today);

        // 要不要弹主面板由调用方决定（见 App.ArchiveFromFloatingQuietlyAsync）——
        // 走到这里就说明用户没关那个开关，那就切到归档页给他看。
        SwitchTo(NavPage.Files);

        var results = await IntakeAsync(paths);
        if (results == null) return new("归档出错，详情看主面板左下角", 0);

        return new("已记入今天：" + FileIntakeService.Summarize(results),
            FileIntakeService.AcceptedCount(results));
    }

    /// <summary>
    /// 静默归档（悬浮窗那条、且用户关掉了「自动打开主面板」）之后，
    /// 让**已经存在**的这个窗口把清单和统计跟上。
    ///
    /// 只刷新，不激活 —— 用户关掉那个开关就是要"别打扰我"。
    /// 窗口还没创建过的话什么都不用做：下次打开时本来就会重新装一遍。
    /// </summary>
    public void RefreshAfterExternalIntake()
    {
        RefreshFileList();
        RefreshStats();
    }

    /// <summary>
    /// 自检用：把主面板拨到指定日期，模拟"用户上次停在那一天"。
    ///
    /// 有这个钩子才能验到那条要紧的规则：悬浮窗拖入**一律记到今天**，
    /// 不管主面板当时正翻在哪一天。用户平时会碰到（前一天忘了记、今天想补），
    /// 但探针不先翻走的话，主面板恰好停在今天，这条规则对不对根本测不出来。
    /// </summary>
    public void SetViewingDateForProbe(DateTime date) => LoadDay(date);

    /// <summary>诊断探针正在灌临时数据。期间不落盘、不刷统计，免得把测试数据写进用户记录。</summary>
    private bool _probeMode;

    /// <summary>自检用：假装"这一天已经收工定格成这个数"（null = 没有定格值）。</summary>
    private int? _probeFrozen;

    /// <summary>
    /// 自检用：往事项列表里灌一份临时数据，验完由调用方自己 LoadDay 恢复。
    ///
    /// 走的是和 LoadDay 同一套 Attach/Detach，只是数据刻意不落盘 ——
    /// 为了让进度条渲染出来就往用户当天的记录里塞几条假事项，那是不能接受的。
    ///
    /// <paramref name="dateForProbe"/> 假装"正在看这一天"（验"翻回旧日期看到的是定格值"），
    /// <paramref name="frozenForProbe"/> 假装那天的定格值。
    /// 两者都只在探针里生效，不碰数据源 —— 真实数据里那天的定格值不该被自检读到，
    /// 更不该被自检改写。
    /// </summary>
    public void LoadEntriesForProbe(
        IReadOnlyList<NoteEntry> entries, DateTime? dateForProbe = null, int? frozenForProbe = null)
    {
        _probeMode = true;
        _probeFrozen = frozenForProbe;
        if (dateForProbe.HasValue) _currentDate = dateForProbe.Value;

        _syncing = true;
        DetachEntries();
        _entries.Clear();
        foreach (var entry in entries)
        {
            AttachEntry(entry);
            _entries.Add(entry);
        }
        _syncing = false;

        EntryList.UpdateLayout();
        RefreshDayCompletion();
    }

    /// <summary>
    /// 自检用：汇报列表底部那行「当天完成度」的**实际控件状态**。
    ///
    /// 和进度条那份报告同一个道理：回头读数据模型没有意义 ——
    /// 「模型里算得出 54%」和「界面上真的写着 54%」隔着绑定和模板两层。
    /// 格式：可见性|数值文字|说明文字|徽标文字|数值颜色|x=行左边界|tx=说明文字左边界|fx=上面每行内容的左边界|pad=行左内边距
    ///
    /// 最后几个是"文字有没有贴住最左边"的判据（2026-10-14 用户要求左对齐、去掉那条汇总条）：
    /// `tx` 要和 `fx`（上面每一行勾选框的左边界）对齐。文字跑到中间去不会有任何报错，只会
    /// "看着不对"，而且绝对坐标换个主题、换个字号就变 —— 只有两个控件互相比才说明得了问题。
    /// </summary>
    public string DayCompletionReport()
    {
        UpdateLayout();
        EntryList.UpdateLayout();
        DayCompletionRow.UpdateLayout();

        var color = DayCompletionValue.Foreground is SolidColorBrush brush
            ? brush.Color.ToString()
            : "(不是纯色)";

        // 两个都量一次：一行文字的左边和整行容器的左边差多少。
        // 基准刻意取"上面每一行勾选框的左边界"而不是本行的 Padding：
        // 本行的 Padding 是它自己的一部分，拿它当基准等于自证；而且边框厚度也要算进去，
        // 差 1 像素都说明"看着齐了其实没齐"。和上面每行内容起点对齐，才是用户说的"最左边"。
        var rowOrigin = DayCompletionRow.TranslatePoint(new Point(0, 0), this);
        var textOrigin = DayCompletionLabel.TranslatePoint(new Point(0, 0), this);
        var rowContentX = DiagnosticsService.Descendants(EntryList).OfType<CheckBox>()
            .Select(c => c.TranslatePoint(new Point(0, 0), this).X)
            .DefaultIfEmpty(double.NaN)
            .First();

        return string.Join("|", new[]
        {
            DayCompletionRow.Visibility.ToString(),
            DayCompletionValue.Text,
            DayCompletionLabel.Text,
            DayCompletionChipText.Text,
            color,
            $"x={rowOrigin.X:0.#}",
            $"tx={textOrigin.X:0.#}",
            $"fx={rowContentX:0.#}",
            $"pad={DayCompletionRow.Padding.Left:0.#}",
        });
    }

    /// <summary>
    /// 自检用：汇报每行进度条的**实际控件状态**。
    ///
    /// 刻意读可视树里的 Slider 和那串百分比文字，而不是回头读数据模型：
    /// 「模型里有进度」和「界面上画出来了、拖得动」是两件事，中间隔着一层模板绑定。
    /// 2026-09-20 那个「设置里点新形象没反应」就是纯逻辑断言全过、控件是灰的。
    ///
    /// 每行格式：事项文字|条的值|旁边的百分比文字|条的宽度|可交互
    /// </summary>
    public string ProgressBarsReport()
    {
        EntryList.UpdateLayout();

        var lines = new List<string>();

        foreach (var grid in DiagnosticsService.Descendants(EntryList).OfType<Grid>())
        {
            var slider = grid.Children.OfType<Slider>().FirstOrDefault();
            if (slider == null) continue;

            var entry = slider.DataContext as NoteEntry;
            var percent = grid.Children.OfType<TextBlock>()
                .Select(t => t.Text)
                .FirstOrDefault(t => t.EndsWith('%')) ?? "(没有百分比文字)";

            lines.Add(
                $"{entry?.Text ?? "(没有数据上下文)"}|" +
                $"{slider.Value:0}|{percent}|" +
                $"w={slider.ActualWidth:0}|" +
                $"{(slider.IsEnabled && slider.IsHitTestVisible ? "可拖" : "不可交互")}");
        }

        return string.Join(" ; ", lines);
    }

    /// <summary>
    /// 等这个窗口加载完。
    ///
    /// 悬浮窗拖入时主面板可能是**这一刻才创建**的：构造函数虽然已经把列表和日期装好了，
    /// 但 Show 之后的布局还没跑。归档结果要落到列表上，宁可在这里等一下。
    /// </summary>
    public Task WaitLoadedAsync()
    {
        if (IsLoaded) return Task.CompletedTask;

        var tcs = new TaskCompletionSource();
        void OnLoaded(object? s, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            tcs.TrySetResult();
        }

        Loaded += OnLoaded;
        return tcs.Task;
    }


    // ==================================================================
    // 公共刷新与辅助
    // ==================================================================

    private void OnStoreChanged()
    {
        // Store.Changed 可能在后台线程的收尾阶段触发，切回 UI 线程再刷
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnStoreChanged));
            return;
        }

        RefreshFileList();
        RefreshStats();
    }

    /// <summary>外部（悬浮窗 / 提醒）触发时刷新一下显眼的统计数字。</summary>
    public void RefreshFromOutside()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshFromOutside));
            return;
        }
        RefreshStats();
    }

    private void SetStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetStatus(text)));
            return;
        }
        StatusText.Text = text;
    }

    /// <summary>供 App 在托盘菜单 / 快捷方式等外部操作后写一行状态。</summary>
    public void SetStatusMessage(string text) => SetStatus(text);

    /// <summary>状态栏当前那行字。诊断自检用它验"跳转有没有给出反馈"。诊断自检用。</summary>
    public string StatusLine => StatusText.Text;

    private void Debounce(Action action)
    {
        _pendingAction = action;
        _debounce.Stop();
        _debounce.Start();
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
