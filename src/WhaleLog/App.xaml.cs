using System.Collections.Specialized;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using WhaleLog.Models;
using WhaleLog.Services;
using WhaleLog.Views;

namespace WhaleLog;

/// <summary>
/// 组合根：所有服务在这里创建、接线、启停。
///
/// 没有引入依赖注入容器 —— 服务数量固定（不到十个）、生命周期都是单例，
/// 手写装配比配容器更好读，也少一个包依赖。
/// </summary>
public partial class App : Application
{
    private const string MutexName = "Local\\WhaleLog.SingleInstance";

    private Mutex? _singleInstance;
    private TrayService? _tray;
    private TrayMenuWindow? _trayMenu;
    private MainWindow? _mainWindow;
    private FloatingWindow? _floatingWindow;
    private QuickCaptureWindow? _quickCapture;
    private ReportWindow? _reportWindow;
    private bool _exiting;

    /// <summary>这次运行是一次性命令（自检 / 截图 / 导出素材）。这类运行不写用户的数据文件。</summary>
    private bool _oneShot;

    public static SettingsService Settings { get; private set; } = null!;
    public static ThemeService Theme { get; private set; } = null!;
    public static LogBookStore Store { get; private set; } = null!;
    public static ClassifyService Classify { get; private set; } = null!;
    public static VersionService Versioner { get; private set; } = null!;
    public static FileIntakeService Intake { get; private set; } = null!;
    public static ReminderService Reminder { get; private set; } = null!;
    public static StartupService AutoStart { get; private set; } = null!;

    /// <summary>
    /// 是否已经有另一个鲸记实例在跑。
    ///
    /// 自检用得上：它允许在程序运行时执行，此时全局快捷键必然抢不到 ——
    /// 那是"被自己占用"，不是"被别的程序占用"，两者的结论完全不同。
    /// </summary>
    public static bool IsAnotherInstanceRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var existing))
            {
                existing.Dispose();
                return true;
            }
        }
        catch
        {
            // 拿不到就当没有，宁可少提示也不误报
        }
        return false;
    }

    /// <summary>实例访问入口：实例成员（快捷键、窗口管理）从界面代码里这样拿。</summary>
    public static App Instance => (App)Current;

    /// <summary>全局快捷键。快速记录靠它呼出。</summary>
    public HotKeyService HotKeys { get; } = new();

    /// <summary>最近一次快捷键注册失败的原因，没有失败时为 null。</summary>
    public string? HotKeyError { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 既认 `--shot`，也认 `--shot=值` —— 只做全等比较的话，
        // `--shot=路径` 会被当成"没有这个开关"，程序就按普通方式启动了：
        // 不退出、一直开着，命令行那边只能看到"卡住"。
        // 解析放在 CommandLine 里（可被自测直接验证）：开关取值的边界情况
        // 在界面上完全看不出来，踩过坑 —— 见那个类的注释。
        var cmd = new CommandLine(e.Args);

        bool HasFlag(string flag) => cmd.Has(flag);

        string? ArgValue(string flag) => cmd.Value(flag);

        // 一次性命令：跑一段逻辑就退出，不是"又开了一个鲸记"。
        // 它们必须绕开单实例守卫 —— 否则程序正在运行时想跑一次自检，
        // 会被守卫拦下来弹个"已经在运行"，然后什么都不做，
        // 用户只能看到一个莫名其妙的提示、日志却没有任何更新。
        bool oneShot = HasFlag("--selfcheck") || HasFlag("--create-shortcut")
                    || HasFlag("--shot") || HasFlag("--shot-notes") || HasFlag("--shot-settings")
                    || HasFlag("--shot-floating") || HasFlag("--shot-calendar")
                    || HasFlag("--shot-anim-menu")
                    || HasFlag("--shot-recent") || HasFlag("--shot-file-calendar")
                    || HasFlag("--shot-edit")
                    || HasFlag("--export-assets");

        if (!oneShot)
        {
            _singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                MessageBox.Show("鲸记已经在运行了，看看托盘图标或桌面上的小鲸鱼。",
                    "鲸记 WhaleLog", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        _oneShot = oneShot;

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.Handled = true;
        };

        BuildServices(writeTodayNote: !oneShot);

        // 只把出厂素材写到磁盘，不启界面。打包脚本用它把素材目录放进发布包 ——
        // 用户解压后立刻能看到该换哪些文件，不用等首次启动。
        if (HasFlag("--export-assets"))
        {
            var dir = ArgValue("--export-assets");
            var written = IconAssets.ExportDefaults(dir);

            if (!HasFlag("--quiet"))
            {
                MessageBox.Show(
                    written.Count == 0
                        ? "素材目录已是最新，没有需要写出的文件。"
                        : $"已写出 {written.Count} 个文件：\n{string.Join("\n", written)}",
                    "鲸记 · 导出素材", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        // 诊断模式：把快捷键、图标、快捷方式、报告生成这些"点一下才知道"的环节
        // 挨个跑一遍并写日志，然后直接退出。
        if (HasFlag("--selfcheck"))
        {
            // 自检里有一节要验"拖文件到小鲸鱼上会不会自动打开主面板"，
            // 会真的把主面板建出来。让它待在屏幕外，别糊在用户桌面上。
            OffscreenWindowProbe = true;

            var path = DiagnosticsService.WriteReport(out var content);
            var conclusion = content
                .Split('\n')
                .LastOrDefault(l => l.StartsWith("结论：", StringComparison.Ordinal))
                ?.Trim() ?? "";

            // --quiet 供脚本调用：只写日志不弹窗
            if (!HasFlag("--quiet"))
            {
                    MessageBox.Show(
                    $"{conclusion}\n\n详细结果已写入：\n{path}",
                    "鲸记 · 诊断自检", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            // 退出码跟着结论走。原先无论成败都返回 0，脚本里
            // `WhaleLog.exe --selfcheck --quiet && echo 没问题` 会一路绿灯走下去 ——
            // 那正是"断言形同虚设"最隐蔽的一种：报了错，但没有任何机制会停下来。
            var passed = conclusion.Contains("全部通过", StringComparison.Ordinal);
            Shutdown(passed ? 0 : 1);
            return;
        }

        // 截图模式：把悬浮窗在最小 / 默认 / 最大三档下渲染成一张 PNG。
        if (HasFlag("--shot-floating"))
        {
            var target = ArgValue("--shot-floating")
                         ?? Path.Combine(AppPaths.LogDir, "floating-scales.png");
            try
            {
                DiagnosticsService.WriteFloatingShot(target);
                if (!HasFlag("--quiet"))
                    MessageBox.Show($"已保存到：\n{target}", "鲸记 · 悬浮窗截图",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                if (!HasFlag("--quiet"))
                    MessageBox.Show(ex.Message, "鲸记 · 悬浮窗截图失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

        // 截图模式：把「编辑记录」窗单独渲染成 PNG。
        // 它是个独立窗口，--shot 系列拍主窗口抓不到；而窗里全是"排版对不对"的事
        // （底部提示和按钮会不会压住、七个分类格子排不排得下），只能看图。
        if (HasFlag("--shot-edit"))
        {
            var target = ArgValue("--shot-edit")
                         ?? Path.Combine(AppPaths.LogDir, "edit-entry.png");
            try
            {
                DiagnosticsService.WriteEditEntryShot(target);
                if (!HasFlag("--quiet"))
                    MessageBox.Show($"已保存到：\n{target}", "鲸记 · 编辑窗截图",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                if (!HasFlag("--quiet"))
                    MessageBox.Show(ex.Message, "鲸记 · 编辑窗截图失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

        // 截图模式：把月历面板单独放大渲染成 PNG。
        // 月历挂在 Popup 里（独立 HWND），上面的 --shot 系列拍不到它，只能单独开一条；
        // 格子里"数字 + 小圆点 + 今天描边"三层叠着，差一个像素只能靠放大看图判断。
        if (HasFlag("--shot-calendar"))
        {
            var target = ArgValue("--shot-calendar")
                         ?? Path.Combine(AppPaths.LogDir, "calendar.png");
            try
            {
                DiagnosticsService.WriteCalendarShot(target);
                if (!HasFlag("--quiet"))
                    MessageBox.Show($"已保存到：\n{target}", "鲸记 · 月历截图",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                if (!HasFlag("--quiet"))
                    MessageBox.Show(ex.Message, "鲸记 · 月历截图失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

        // 截图模式：把托盘菜单渲染成 PNG（一级页 + 「自选播放动画」二级页各一张）。
        // 二级页是这里唯一"靠滚动和筛选撑住上百项"的界面 ——
        // 分组标题挤不挤、动作名有没有被截断，断言一句都问不出来，只能看图。
        if (HasFlag("--shot-anim-menu"))
        {
            var target = ArgValue("--shot-anim-menu")
                         ?? Path.Combine(AppPaths.LogDir, "anim-menu.png");
            try
            {
                // 挑一个**真有动画素材**的形象包来出图：用户可能正用着原版（没有 anim 目录），
                // 那样二级页拍出来是空的 —— 一张什么都核销不了的图。
                var lib = CharacterPacks.Available()
                              .Select(p => AnimLibrary.Scan(p.Root))
                              .FirstOrDefault(l => l.HasAny)
                          ?? AnimLibrary.Scan();

                var mainMenu = new TrayMenuWindow();
                BuildMainMenu(mainMenu, 0, 0);
                DiagnosticsService.WriteMenuShot(mainMenu, target);

                var subMenu = new TrayMenuWindow();
                BuildAnimationMenu(subMenu, 0, 0, lib);
                var subTarget = Path.Combine(
                    Path.GetDirectoryName(target) ?? ".",
                    Path.GetFileNameWithoutExtension(target) + "-sub.png");
                DiagnosticsService.WriteMenuShot(subMenu, subTarget);

                if (!HasFlag("--quiet"))
                    MessageBox.Show($"一级页：{target}\n二级页：{subTarget}", "鲸记 · 菜单截图",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                if (!HasFlag("--quiet"))
                    MessageBox.Show(ex.Message, "鲸记 · 菜单截图失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

        // 截图模式：把往期清单面板单独放大渲染成 PNG。
        // 和月历同理 —— 它也在 Popup 里（独立 HWND），上面的 --shot 系列拍不到。
        if (HasFlag("--shot-recent"))
        {
            var target = ArgValue("--shot-recent")
                         ?? Path.Combine(AppPaths.LogDir, "recent-days.png");
            try
            {
                DiagnosticsService.WriteRecentShot(target);
                if (!HasFlag("--quiet"))
                    MessageBox.Show($"已保存到：\n{target}", "鲸记 · 往期清单截图",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                if (!HasFlag("--quiet"))
                    MessageBox.Show(ex.Message, "鲸记 · 往期清单截图失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

        // 截图模式：把归档日历（文件归档页那个"蓝点 + 当天归档数"）单独放大渲染成 PNG。
        // 它也在 Popup 里，和上面两条同理。
        if (HasFlag("--shot-file-calendar"))
        {
            var target = ArgValue("--shot-file-calendar")
                         ?? Path.Combine(AppPaths.LogDir, "file-calendar.png");
            try
            {
                DiagnosticsService.WriteFileCalendarShot(target);
                if (!HasFlag("--quiet"))
                    MessageBox.Show($"已保存到：\n{target}", "鲸记 · 归档日历截图",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                if (!HasFlag("--quiet"))
                    MessageBox.Show(ex.Message, "鲸记 · 归档日历截图失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

        // 截图模式：把主面板的某一页渲染成 PNG。
        // 自检能断言"有几个栏头""卡片有没有叠住"，但断言不了"看起来对不对"（文字被裁、
        // 颜色糊、对齐歪），需要肉眼确认时用它出一张图；渲染的是离屏窗口，不会弹到用户面前。
        //   --shot <png>            文件归档页（「全部」筛选，能看到跨天分组）
        //   --shot-notes <png>      今日记录页（当天的事项和归档文件）
        //   --shot-settings <png>   设置页 —— 各项开关、滑杆、形象那一排按钮都在这一页，
        //                           "按钮该亮还是该灰"这种事只能看图确认
        //   （「编辑记录」窗也有一条独立出口 --shot-edit，不在这里：
        //     它是独立窗口，拍主面板的这几条都抓不到它，所以在上面单独处理。）
        var shotPage = HasFlag("--shot-notes") ? NavPage.Notes
                     : HasFlag("--shot-settings") ? NavPage.Settings
                     : HasFlag("--shot") ? NavPage.Files
                     : (NavPage?)null;

        if (shotPage != null)
        {
            var flag = shotPage switch
            {
                NavPage.Notes => "--shot-notes",
                NavPage.Settings => "--shot-settings",
                _ => "--shot",
            };
            var fallback = shotPage switch
            {
                NavPage.Notes => "notes-page.png",
                NavPage.Settings => "settings-page.png",
                _ => "files-page.png",
            };
            var target = ArgValue(flag) ?? Path.Combine(AppPaths.LogDir, fallback);
            try
            {
                DiagnosticsService.WritePageShot(target, shotPage.Value);
                if (!HasFlag("--quiet"))
                    MessageBox.Show($"已保存到：\n{target}", "鲸记 · 界面截图",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                if (!HasFlag("--quiet"))
                    MessageBox.Show(ex.Message, "鲸记 · 界面截图失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

        // 构建脚本用：发布完顺便在桌面放一个快捷方式，然后退出。
        // 放在程序里做而不是写在 .cmd 里，是为了避开批处理文件的中文编码地雷。
        if (e.Args.Any(a => a.Equals("--create-shortcut", StringComparison.OrdinalIgnoreCase)))
        {
            var ok = ShortcutService.TryCreate(out var shortcutMessage);
            if (!HasFlag("--quiet"))
            {
                MessageBox.Show(shortcutMessage, "鲸记 · 桌面快捷方式",
                    MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }

        bool startHidden = e.Args.Any(
            a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        // 首次运行把内置素材导出到 Assets\，用户打开目录就知道该换哪个文件。
        // 只写"还不存在"的文件，所以对已经换过素材的人是一次空操作。
        // 刻意不放在一次性命令（--selfcheck / --shot）里 —— 诊断命令不该产生副作用。
        IconAssets.ExportDefaults();

        CreateTray();
        StartReminder();
        ApplyQuickCaptureHotKey();

        // 今天之前的每一天：把完成度定格在收工时的数字上。
        // 顺序刻意在顺延之前 —— 先给昨天结账，再把没做完的事搬到今天。
        // 和顺延一样放在这里而不是 BuildServices 里：一次性命令（自检 / 截图）不该顺手改用户的数据。
        RunCompletionFreeze(announce: false);

        // 未完成的事项顺延到今天。
        // 刻意放在这里而不是 BuildServices 里：一次性命令（自检 / 截图）不该顺手改用户的数据。
        // 也刻意在 ShowMainWindow() 之前 —— 主面板随后才第一次 LoadDay，天然就能看到顺延来的条目。
        RunCarryOver(announce: false);

        if (!startHidden)
            ShowMainWindow();

        if (Settings.Settings.FloatingEnabled)
            ShowFloating();
    }

    private void BuildServices(bool writeTodayNote = true)
    {
        AppPaths.EnsureCreated();

        Settings = new SettingsService();
        Settings.Load();

        Theme = new ThemeService();
        Theme.Start();
        Theme.ForceApply();

        Store = new LogBookStore();
        Store.Load();

        // 一次性命令（自检 / 快捷方式）不碰数据文件：
        // 万一此时正有另一个实例在跑，避免两个进程同时写 index.json。
        if (writeTodayNote) Store.EnsureToday();

        Classify = new ClassifyService();
        Classify.Load();

        Versioner = new VersionService(Settings);

        Intake = new FileIntakeService(Store, Classify, Versioner, Settings);

        AutoStart = new StartupService();
        // 让注册表和设置保持一致：用户可能在别的工具里删掉了启动项
        bool actuallyEnabled = AutoStart.IsEnabled();
        if (actuallyEnabled != Settings.Settings.StartWithWindows)
            Settings.Update(s => s.StartWithWindows = actuallyEnabled);

        Reminder = new ReminderService(Settings, BuildDayStatus);
    }

    /// <summary>供提醒服务判断语气：今天记了几条、收了几条、归了几个文件。</summary>
    private DayStatus BuildDayStatus()
    {
        var today = DateTime.Today;
        var day = Store.Book.FindDay(today);
        int files = Store.FilesOnDate(today).Count();
        return day == null
            ? new DayStatus(0, 0, 0, files)
            : new DayStatus(day.Entries.Count, day.OpenCount, day.DoneCount, files);
    }

    private void CreateTray()
    {
        _tray = new TrayService();
        _tray.OpenRequested += ShowMainWindow;
        _tray.MenuRequested += ShowTrayMenu;
    }

    private void StartReminder()
    {
        Reminder.ReminderFired += OnReminderFired;
        Reminder.Ticked += UpdateTrayTooltip;
        Reminder.Ticked += OnReminderTick;
        Reminder.Start();
        UpdateTrayTooltip();
    }

    /// <summary>上次做过跨天检查的日期。</summary>
    private string _carryCheckedDate = DateTime.Today.ToString("yyyy-MM-dd");

    /// <summary>
    /// 每 20 秒的轮询里顺手看一眼有没有跨天。
    ///
    /// 只在启动时跑一次顺延是不够的：这程序常驻托盘、通宵开着是常态，
    /// 那样子一早就看不到昨天下班前没勾完的事项了。
    /// </summary>
    private void OnReminderTick()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (today == _carryCheckedDate) return;
        _carryCheckedDate = today;

        // 跨零点这一下就是"次日 0 点前保存当天完成度"的落点：先给刚过去的那天结账，再开工。
        RunCompletionFreeze(announce: true);
        RunCarryOver(announce: true);
    }

    /// <summary>
    /// 跑一次完成度定格（把"今天之前"还没结账的日子写死成收工时的数字）。
    ///
    /// announce = 跨零点自动触发（弹个气泡说一声），false = 启动时
    /// （主面板还没建，写状态栏没意义，界面上那一行自己会呈现）。
    /// </summary>
    private void RunCompletionFreeze(bool announce)
    {
        CompletionService.FreezeReport report;
        try
        {
            report = CompletionService.FreezePastDays(Store.Book);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            return;
        }

        if (!report.Any) return;

        Store.Save();

        // 列表有可能正开着（跨天那次），让它把「收工定格」的徽标换上来
        _mainWindow?.RefreshCurrentDay();
        _mainWindow?.SetStatusMessage(report.Describe());

        if (announce)
        {
            try { _floatingWindow?.ShowBubble("收工结账", report.Describe()); }
            catch { /* 悬浮窗可能没开 */ }
        }
    }

    /// <summary>
    /// 跑一次顺延。announce = 跨天自动触发（需要弹气泡告诉用户），
    /// false = 启动时（主面板还没建，写状态栏没意义，靠列表自己呈现就够了）。
    /// </summary>
    private void RunCarryOver(bool announce)
    {
        CarryOverService.CarryReport report;
        try
        {
            report = CarryOverService.Run(Store);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            return;
        }

        if (!report.Any) return;

        // 列表有可能正开着（跨天那次），把新出现的条目补进去
        _mainWindow?.RefreshCurrentDay();
        _mainWindow?.SetStatusMessage(report.Describe());

        if (announce)
        {
            try { _floatingWindow?.ShowBubble("新的一天", report.Describe(), 1); }
            catch { /* 悬浮窗可能没开 */ }
        }
    }

    private void UpdateTrayTooltip()
    {
        if (_tray == null) return;
        var status = BuildDayStatus();
        _tray.SetTooltip(
            $"鲸记 WhaleLog\n{Reminder.RemainingText}\n今天 {status.EntryCount} 条记录 / {status.FileCount} 个文件");
    }

    private void OnReminderFired(ReminderEvent e)
    {
        // 三个渠道相互独立：悬浮窗气泡、音效、托盘通知，任意一个失败不影响其他两个
        try { _floatingWindow?.ShowBubble(e.Title, e.Message, e.Urgency); }
        catch { /* 悬浮窗可能已被关闭 */ }

        // 到点下班是小鲸鱼会主动庆祝的两个时刻之一（另一个是"今天最后一条收尾"，
        // 悬浮窗自己盯着状态刷新，见 FloatingWindow.NoteWorkFinished）。
        // "哪几种提醒值得演一段"这条判断在悬浮窗里（FloatingWindow.PlayForReminder），
        // 这里只负责把 Kind 递过去 —— 那个方法不带副作用，自检验得了。
        try { _floatingWindow?.PlayForReminder(e.Kind); }
        catch { /* 同上 */ }

        if (Settings.Settings.ReminderSoundEnabled)
        {
            try { System.Media.SystemSounds.Exclamation.Play(); }
            catch { /* 没有音频设备时忽略 */ }
        }

        if (Settings.Settings.ReminderTrayBalloonEnabled)
            _tray?.ShowBalloon(e.Title, e.Message, warning: e.Urgency >= 2);
    }

    // ------------------------------------------------------------------
    // 窗口管理
    // ------------------------------------------------------------------

    public void ShowMainWindow()
    {
        try
        {
            if (_mainWindow == null)
            {
                _mainWindow = new MainWindow();

                // 自检要在屏幕外建一个**真实的**主面板来验"拖入后会不会自动打开"，
                // 这个开关就是给它用的 —— 不然自检会在用户桌面上弹出一个窗口。
                if (OffscreenWindowProbe)
                {
                    _mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                    _mainWindow.ShowInTaskbar = false;
                    _mainWindow.Left = -32000;
                    _mainWindow.Top = -32000;
                }

                _mainWindow.Closed += (_, _) => _mainWindow = null;
            }

            if (!_mainWindow.IsVisible) _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;

            _mainWindow.Activate();
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
            _mainWindow.Focus();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    public void ShowFloating()
    {
        try
        {
            _floatingWindow ??= new FloatingWindow();
            _floatingWindow.Show();
            _floatingWindow.ApplySettings();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    /// <summary>
    /// 悬浮窗拖入文件时用：把主面板叫出来，并把它交给调用方。
    ///
    /// 和 <see cref="ShowMainWindow"/> 只差一点：主面板可能是这一刻才创建的，
    /// 这时要等它加载完再返回 —— 紧接着的归档结果要显示在列表上，
    /// 打在还没加载完的窗口上会白做。
    /// </summary>
    public async Task<MainWindow?> ShowMainWindowForDropAsync()
    {
        ShowMainWindow();
        if (_mainWindow is not { } main) return null;

        await main.WaitLoadedAsync();
        return main;
    }

    /// <summary>
    /// 悬浮窗拖入文件、但用户关掉了「自动打开主面板」时的**静默归档**。
    ///
    /// 归档本身是 <see cref="Intake"/> 干的活，主面板只是它的显示层 ——
    /// 所以不进主面板也能把文件收下。已经开着的那个主面板（用户自己开的）只让它
    /// 把列表和统计跟上，**不会被叫到前面来**：关掉这个开关的意图就是"别打扰我"。
    ///
    /// 返回给悬浮窗做气泡提示的一句话，以及"真收下了几个文件"——
    /// 后者是悬浮窗决定"要不要演一段庆祝"的依据（见 FloatingWindow 的归档表演）。
    /// </summary>
    public async Task<FileIntakeService.IntakeOutcome> ArchiveFromFloatingQuietlyAsync(string[] paths)
    {
        IReadOnlyList<IntakeResult> results;

        try
        {
            // 一律记入今天，和「会自动打开主面板」那条路完全一致 ——
            // 两个开关只差"要不要弹面板"，归档行为必须一模一样。
            results = await Intake.IntakeAsync(paths, DateTime.Today);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            return new("归档出错，详情看日志", 0);
        }

        _mainWindow?.RefreshAfterExternalIntake();

        // 措辞和"会自动打开主面板"那条路保持一致（同样一句 "已记入今天：…"）——
        // 两条路只差"要不要弹面板"，用户看到的话不该有两种说法。
        return new("已记入今天：" + FileIntakeService.Summarize(results),
            FileIntakeService.AcceptedCount(results));
    }

    // ------------------------------------------------------------------
    // 诊断自检用的入口
    // ------------------------------------------------------------------

    /// <summary>
    /// 让接下来创建的主面板待在屏幕外。对应悬浮窗的 <c>BackgroundProbeMode</c>，
    /// 只在 <c>--selfcheck</c> 里打开，正常运行全程为 false。
    /// </summary>
    public bool OffscreenWindowProbe { get; set; }

    /// <summary>自检用：当前的主面板实例（没创建过时为 null）。</summary>
    public MainWindow? MainWindowForProbe => _mainWindow;

    /// <summary>自检用：把主面板收掉，别把它留在用户屏幕上。</summary>
    public void CloseMainWindowForProbe()
    {
        try { _mainWindow?.Close(); } catch { /* 关不掉不影响结论 */ }
        _mainWindow = null;
    }

    public void HideFloating() => _floatingWindow?.Hide();

    /// <summary>
    /// 当前悬浮窗（没创建或已隐藏时为 null）。
    /// 设置页拖滑杆时用它做实时预览，不用等设置落盘的节流。
    /// </summary>
    public FloatingWindow? Floating =>
        _floatingWindow is { IsVisible: true } ? _floatingWindow : null;

    public void ToggleFloating()
    {
        if (_floatingWindow is { IsVisible: true }) HideFloating();
        else ShowFloating();
    }

    /// <summary>
    /// 开关小鲸鱼的动态素材。
    ///
    /// 不用手动去通知悬浮窗：改设置会抛 <c>Changed</c>，悬浮窗的 ApplySettings
    /// 发现这个开关翻动了就会重建播放器。
    /// </summary>
    private void ToggleFloatingAnimations()
    {
        Settings.Update(s => s.FloatingAnimations = !s.FloatingAnimations);
        var on = Settings.Settings.FloatingAnimations;

        _floatingWindow?.ShowBubble(
            on ? "小鲸鱼动画已开启" : "小鲸鱼动画已关闭",
            on
                ? "会播放素材目录 anim\\ 下的精灵图动画。"
                : "回到静态形象；素材文件不用删。");
    }

    private void ShowTrayMenu(int x, int y)
    {
        try
        {
            _trayMenu?.Close();
            _trayMenu = new TrayMenuWindow();

            BuildMainMenu(_trayMenu, x, y);

            _trayMenu.ShowAt(x, y);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    /// <summary>
    /// 一级菜单。
    ///
    /// 抽成独立方法是给二级页的「← 返回」用的 —— 返回要把同一套内容原样重建一遍，
    /// 而不是"关掉再开一个新的"（那样位置会重新算，用户点的那个位置就飘了）。
    /// </summary>
    private void BuildMainMenu(TrayMenuWindow menu, int x, int y)
    {
        var s = Settings.Settings;
        var hotkey = string.IsNullOrWhiteSpace(s.QuickCaptureHotkey) ? "未设置" : s.QuickCaptureHotkey;

        menu.AddItem($"快速记录（{hotkey}）", ShowQuickCapture);
        menu.AddItem("打开主面板", ShowMainWindow);
        menu.AddItem("生成今日日报", () => ShowDailyReport(DateTime.Today));
        menu.AddItem("生成本周周报", () => ShowWeeklyReport(DateTime.Today));
        menu.AddItem("桌面小鲸鱼", ToggleFloating, s.FloatingEnabled, isLast: true);
        menu.AddItem("小鲸鱼动画", ToggleFloatingAnimations, s.FloatingAnimations);
        // 素材包有一百多个动作，全铺进一级就彻底没法看了 —— 收进二级页（见下面那个方法）
        menu.AddNavItem("播放动画…", () => BuildAnimationMenu(menu, x, y));
        menu.AddItem("今天别再提醒", () =>
        {
            Settings.Update(x2 => x2.ReminderEnabled = false);
            _floatingWindow?.ShowBubble("已关闭今日提醒", "重新打开：托盘菜单 → 恢复提醒。");
        });
        menu.AddItem("恢复提醒", () =>
        {
            Settings.Update(x2 => x2.ReminderEnabled = true);
            Reminder.ResetToday();
        });
        menu.AddItem("创建桌面快捷方式", () => CreateDesktopShortcut(), isLast: true);
        menu.AddItem("打开数据目录", OpenDataFolder);
        menu.AddItem("打开素材目录", OpenAssetsFolder);
        menu.AddItem("重新加载素材", ReloadAssets);
        menu.AddItem("退出鲸记", ExitApp);
    }

    /// <summary>
    /// 二级页：自选播放动画。
    ///
    /// 原样照搬一级的做法做不到 —— dsh 那套素材光是"闲置小动作"就有九十多个，
    /// 名字还各不相同，光按角色分组根本压不短。所以这里给一个筛选框，
    /// 列表本身按角色分组，输入关键词才平铺。
    /// </summary>
    private void BuildAnimationMenu(TrayMenuWindow menu, int x, int y, AnimLibrary? library = null)
    {
        menu.ClearItems();

        menu.AddNavItem("返回菜单", () =>
        {
            menu.ClearItems();
            BuildMainMenu(menu, x, y);
            menu.Relayout();
        }, isBack: true);

        // 默认用**当前形象包**扫出来的那份 —— 菜单不自己另扫一遍，
        // 否则「重新加载素材」之后，菜单里列的和界面上真能播的会对不上。
        // 自检会传一份自己造的素材进来，好让主路径在隔离环境下也能被验到。
        var lib = library ?? _floatingWindow?.Animations;
        if (lib == null || !lib.HasAny)
        {
            menu.AddHeader("没扫到能播的动作");
            menu.AddItem("打开素材目录", OpenAssetsFolder);
            menu.AddItem("重新加载素材", ReloadAssets);
            menu.Relayout();
            return;
        }

        // 两个开关关着时，点了是不会有任何动静的 —— 那正是最难查的"点了没反应"。
        // 先说清楚会自己打开，用户不用两头跑。
        if (!Settings.Settings.FloatingEnabled || !Settings.Settings.FloatingAnimations)
            menu.AddHeader("选一个动作会自动打开桌面小鲸鱼");

        // 搜索框必须排在列表**上面**：这块列表有一百多项，搜索框要是跟在它后面，
        // 用户得先滚到底才找得到 —— 那等于没有。（先加的就是上面的。）
        //
        // section 先声明成可空的：Fill 要交给搜索框当回调，编译器没法证明
        // 它被调用时列表已经建好，所以 Fill 自己判一下（实际不会走到那个分支）。
        TrayMenuWindow.Section? section = null;

        void Fill(string keyword)
        {
            if (section is not { } host) return;

            host.Clear();

            // 行清单由纯函数算出来（见 AnimMenu）：分组、短名、平铺、筛不出东西的提示
            // 全在那边，这里只负责把行变成控件 —— 能测的那部分就别留在界面层。
            foreach (var row in AnimMenu.Build(lib, keyword))
            {
                if (row.Kind == AnimMenuRowKind.Header) host.AddHeader(row.Text);
                else host.AddItem(row.Text, () => PlayChosenAction(row.Action!));
            }
        }

        menu.AddSearchBox(Fill);

        section = menu.AddSection();

        Fill("");

        menu.Relayout();
    }

    /// <summary>
    /// 播放用户选中的动作（走到这里时菜单已经关了）。
    ///
    /// 桌面小鲸鱼没开、动画开关关着，都在这儿补齐：用户点的是"播放动画"，
    /// 不是"研究这两个开关怎么配合"—— 让他点完什么也看不到，才是真的莫名其妙。
    /// </summary>
    private void PlayChosenAction(string action)
    {
        try
        {
            if (!Settings.Settings.FloatingEnabled || !Settings.Settings.FloatingAnimations)
            {
                Settings.Update(s =>
                {
                    s.FloatingEnabled = true;
                    s.FloatingAnimations = true;
                });
            }

            if (_floatingWindow is not { IsVisible: true }) ShowFloating();
            _floatingWindow?.ApplySettings();

            if (_floatingWindow?.PlayAction(action) != true)
            {
                CrashLog.Note($"[动画] 菜单里选的动作没播出来：{action}");
                _floatingWindow?.ShowBubble("这个动作没播出来", "素材可能读不出来，可在菜单里「重新加载素材」。");
            }
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    /// <summary>
    /// 自检用：把「自选播放动画」二级页搭出来（**不显示窗口**），返回它每一行长什么样。
    ///
    /// 素材由调用方给：自检环境里形象包可能是原版（压根没有 anim 素材），
    /// 用真实素材验的就只是"没扫到"那条分支，主路径反而没人管了。
    ///
    /// 菜单是代码构造的、没有数据绑定可查 —— 想知道"里面到底有什么"，
    /// 只能把构建结果念出来核对。少了一截不会报错，只会安静地空着。
    /// </summary>
    public IReadOnlyList<string> AnimationMenuForProbe(AnimLibrary library)
    {
        var probe = new TrayMenuWindow();
        BuildAnimationMenu(probe, 0, 0, library);
        return probe.RowTextsForProbe;
    }

    /// <summary>在鼠标当前位置弹菜单（悬浮窗右键用）。</summary>
    public void ShowTrayMenuAtCursor()
    {
        var p = System.Windows.Forms.Cursor.Position;
        ShowTrayMenu(p.X, p.Y);
    }

    // ------------------------------------------------------------------
    // 快速记录
    // ------------------------------------------------------------------

    /// <summary>按设置重新绑定全局快捷键。</summary>
    public void ApplyQuickCaptureHotKey()
    {
        var s = Settings.Settings;

        try
        {
            HotKeys.ReRegister(
                s.QuickCaptureEnabled ? s.QuickCaptureHotkey : null,
                ShowQuickCapture);

            HotKeyError = s.QuickCaptureEnabled && !HotKeys.HasAnyRegistration
                ? HotKeys.LastError ?? "快捷键注册失败"
                : null;
        }
        catch (Exception ex)
        {
            HotKeyError = ex.Message;
        }
    }

    /// <summary>呼出"记一笔"输入框。已在显示中就把它拉到前面，不重复开窗。</summary>
    public void ShowQuickCapture()
    {
        try
        {
            if (_quickCapture is { IsVisible: true })
            {
                _quickCapture.Activate();
                _quickCapture.Focus();
                return;
            }

            _quickCapture = new QuickCaptureWindow();
            _quickCapture.Captured += OnQuickCaptured;
            _quickCapture.Show();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    private void OnQuickCaptured(Models.NoteEntry entry)
    {
        var suffix = string.IsNullOrWhiteSpace(entry.Tag) ? "" : $"  #{entry.Tag}";
        var preview = entry.Text.Length > 28 ? entry.Text[..28] + "…" : entry.Text;

        _floatingWindow?.ShowBubble($"{entry.TimeText} 记下了", preview + suffix);
        _mainWindow?.RefreshFromOutside();
        UpdateTrayTooltip();
    }

    /// <summary>快速记录窗关闭时把它从缓存里摘掉。</summary>
    public void NotifyQuickCaptureClosed(QuickCaptureWindow window)
    {
        if (ReferenceEquals(_quickCapture, window)) _quickCapture = null;
    }

    // ------------------------------------------------------------------
    // 日报 / 周报
    // ------------------------------------------------------------------

    public void ShowDailyReport(DateTime date)
    {
        try
        {
            var content = ReportService.BuildDaily(Store.Book, date);
            ReportService.TryWriteDaily(Store.Book, date, out var path, out var message);

            ShowReport(
                $"{date:yyyy-MM-dd} 日报",
                $"（{ReportService.WeekdayText(date)}）",
                content,
                path,
                message);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    public void ShowWeeklyReport(DateTime anyDayInWeek)
    {
        try
        {
            var start = ReportService.WeekStart(anyDayInWeek);
            var content = ReportService.BuildWeekly(Store.Book, anyDayInWeek);
            ReportService.TryWriteWeekly(Store.Book, anyDayInWeek, out var path, out var message);

            ShowReport(
                $"{start:yyyy-MM-dd} 起 周报",
                "（含分日明细与文件清单）",
                content,
                path,
                message);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    private void ShowReport(string title, string subtitle, string content, string? path, string hint)
    {
        if (_reportWindow == null)
        {
            _reportWindow = new ReportWindow();
            _reportWindow.Closed += (_, _) => _reportWindow = null;
        }

        _reportWindow.Load(title, subtitle, content, path, hint);

        if (!_reportWindow.IsVisible) _reportWindow.Show();
        _reportWindow.Activate();
    }

    // ------------------------------------------------------------------
    // 桌面快捷方式
    // ------------------------------------------------------------------

    public bool CreateDesktopShortcut()
    {
        var ok = ShortcutService.TryCreate(out var message);

        _floatingWindow?.ShowBubble(ok ? "桌面快捷方式好了" : "创建快捷方式失败", message);
        _mainWindow?.SetStatusMessage(message);
        return ok;
    }

    public static void OpenDataFolder()
    {
        try
        {
            AppPaths.EnsureCreated();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    public static void OpenAssetsFolder()
    {
        try
        {
            // 目录可能还不存在（比如用户手删了），先把内置素材补回去再打开 ——
            // 打开一个空目录对用户毫无帮助。
            Directory.CreateDirectory(AppPaths.AssetsDir);
            IconAssets.ExportDefaults();

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.AssetsDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    /// <summary>
    /// 重新从磁盘读一遍素材。用户在 Assets\ 里换了图之后，不用重启界面。
    /// </summary>
    public void ReloadAssets()
    {
        IconAssets.Reload();

        // 悬浮窗是常驻的，必须显式刷新；主面板是缓存的实例，同理。
        // 其他窗口（快速记录、日报）每次都是新建的，下次打开自然是新素材。
        _floatingWindow?.ReloadAssets();
        _mainWindow?.RefreshAssets();

        // 托盘图标要重新赋值才会重绘
        _tray?.ReloadIcon();

        var used = IconAssets.LoadedFromExternal;
        var summary = used.Count == 0
            ? "当前用的是内置素材。把图片放进素材目录就会自动优先使用。"
            : $"已启用外部素材 {used.Count} 个：" +
              string.Join("、", used.Keys.Select(k => AssetCatalog.Find(k)?.Files[0] ?? k));

        _floatingWindow?.ShowBubble("素材已重新加载", summary);
        _tray?.SetTooltip("鲸记 WhaleLog");
    }

    public void ExitApp()
    {
        _exiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (!_exiting)
            {
                // 托盘「退出」之外的路由（比如会话注销）也要落盘
            }

            Reminder?.Stop();
            Theme?.Stop();

            // 一次性命令（自检 / 截图 / 导出素材）**一个字节都不该写进用户的数据目录**：
            // 它们读数据只是为了渲染或断言，退出时顺手 Save() 会把整个索引重写一遍
            // （内容通常一样，但 mtime 变了、还留下新的 .backup），
            // 于是"跑一趟自检看看有没有问题"变成了一次真实的写操作。
            if (!_oneShot) Store?.Save();

            Settings?.Save();
            _quickCapture?.Close();
            _reportWindow?.Close();
            _floatingWindow?.Close();
            _trayMenu?.Close();
            HotKeys?.Dispose();
            _tray?.Dispose();
            _singleInstance?.Dispose();
        }
        catch
        {
            // 退出路径上不再抛异常
        }

        base.OnExit(e);
    }

    private static void LogCrash(Exception ex) => CrashLog.Write(ex);
}
