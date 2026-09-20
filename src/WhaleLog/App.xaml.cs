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
                    || HasFlag("--shot-floating")
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

            Shutdown();
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

        // 截图模式：把主面板的某一页渲染成 PNG。
        // 自检能断言"有几个栏头""卡片有没有叠住"，但断言不了"看起来对不对"（文字被裁、
        // 颜色糊、对齐歪），需要肉眼确认时用它出一张图；渲染的是离屏窗口，不会弹到用户面前。
        //   --shot <png>            文件归档页（「全部」筛选，能看到跨天分组）
        //   --shot-notes <png>      今日记录页（当天的事项和归档文件）
        //   --shot-settings <png>   设置页 —— 各项开关、滑杆、形象那一排按钮都在这一页，
        //                           "按钮该亮还是该灰"这种事只能看图确认
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
        Reminder.Start();
        UpdateTrayTooltip();
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

            var s = Settings.Settings;
            var hotkey = string.IsNullOrWhiteSpace(s.QuickCaptureHotkey) ? "未设置" : s.QuickCaptureHotkey;

            _trayMenu.AddItem($"快速记录（{hotkey}）", ShowQuickCapture);
            _trayMenu.AddItem("打开主面板", ShowMainWindow);
            _trayMenu.AddItem("生成今日日报", () => ShowDailyReport(DateTime.Today));
            _trayMenu.AddItem("生成本周周报", () => ShowWeeklyReport(DateTime.Today));
            _trayMenu.AddItem("桌面小鲸鱼", ToggleFloating, s.FloatingEnabled, isLast: true);
            _trayMenu.AddItem("小鲸鱼动画", ToggleFloatingAnimations, s.FloatingAnimations);
            _trayMenu.AddItem("今天别再提醒", () =>
            {
                Settings.Update(x2 => x2.ReminderEnabled = false);
                _floatingWindow?.ShowBubble("已关闭今日提醒", "重新打开：托盘菜单 → 恢复提醒。");
            });
            _trayMenu.AddItem("恢复提醒", () =>
            {
                Settings.Update(x2 => x2.ReminderEnabled = true);
                Reminder.ResetToday();
            });
            _trayMenu.AddItem("创建桌面快捷方式", () => CreateDesktopShortcut(), isLast: true);
            _trayMenu.AddItem("打开数据目录", OpenDataFolder);
            _trayMenu.AddItem("打开素材目录", OpenAssetsFolder);
            _trayMenu.AddItem("重新加载素材", ReloadAssets);
            _trayMenu.AddItem("退出鲸记", ExitApp);

            _trayMenu.ShowAt(x, y);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
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
            Store?.Save();
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
