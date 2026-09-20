using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using WhaleLog.Models;
using WhaleLog.Views;

namespace WhaleLog.Services;

/// <summary>
/// 诊断自检。
///
/// 起因很实际：全局快捷键注册、桌面快捷方式创建、托盘图标加载这些路径
/// 在正常使用中都是"点一下才知道好不好"，出问题时也看不出卡在哪一步。
/// 自检把这些环节挨个跑一遍并写出结论，用户把日志发过来就能定位。
///
/// 用 `WhaleLog.exe --selfcheck` 触发，结果写在 logs\selfcheck.log。
/// </summary>
public static class DiagnosticsService
{
    /// <summary>
    /// 断言回调。
    ///
    /// 刻意不用 <c>Action&lt;string, bool, string&gt;</c> —— 它不允许可选参数，
    /// 于是在各探针里写 <c>check("标题", true)</c> 会编译不过，只能到处补一个多余的 <c>""</c>。
    /// 自定义委托声明了 detail 的默认值，两参调用就能用。
    /// </summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

    /// <summary>
    /// 跑一轮自检并返回报告文本。
    ///
    /// 探针为了验证界面会去改内存里的设置对象（比如把浮窗缩放到 1.6 来量有没有被裁）；
    /// 而程序退出时 <c>SettingsService.Save()</c> 会把这个对象整个写回 settings.json，
    /// 探针的改动就"泄漏"进用户配置了。所以整体兜一层快照：
    /// 不管探针怎么改、甚至中途抛异常，跑完都把设置还原回去。
    /// </summary>
    public static string RunReport()
    {
        var snapshot = App.Settings.Snapshot();
        try
        {
            return RunReportCore();
        }
        finally
        {
            App.Settings.Restore(snapshot);
        }
    }

    private static string RunReportCore()
    {
        var sb = new StringBuilder();
        sb.AppendLine("鲸记 WhaleLog · 诊断自检");
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 60));

        var failures = 0;

        void Section(string title)
        {
            sb.AppendLine();
            sb.AppendLine($"【{title}】");
        }

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            sb.AppendLine($"  {(ok ? "✓" : "✗")} {name}{(string.IsNullOrEmpty(detail) ? "" : "：" + detail)}");
        }

        // ---- 运行环境 ----
        Section("运行环境");
        var exe = Environment.ProcessPath ?? "(未知)";
        sb.AppendLine($"  程序：{exe}");
        sb.AppendLine($"  托管入口：{Assembly.GetEntryAssembly()?.Location ?? "(未知)"}");
        sb.AppendLine($"  运行时：{Environment.Version}");
        sb.AppendLine($"  系统：{Environment.OSVersion}");
        var viaMuxer = Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
        sb.AppendLine($"  启动方式：{(viaMuxer ? "dotnet 宿主（框架依赖）" : "直接运行 WhaleLog.exe")}");

        // 核心库在哪儿能一眼看出是框架依赖还是自包含（打包版排查首问）。
        // 框架依赖时它在 C:\Program Files (x86)\dotnet\shared\...；自包含时就在程序目录里。
        var corelib = typeof(object).Assembly.Location;
        var exeDir = Path.GetDirectoryName(exe) ?? "";
        bool appLocal = !string.IsNullOrEmpty(corelib) &&
            string.Equals(Path.GetDirectoryName(corelib), exeDir, StringComparison.OrdinalIgnoreCase);
        sb.AppendLine($"  运行时位置：{(appLocal ? "程序目录内（自包含）" : "系统共享目录（需装 .NET 8 桌面运行时）")}");
        sb.AppendLine($"    {corelib}");

        // ---- 数据目录 ----
        Section("数据目录与落盘");
        try
        {
            AppPaths.EnsureCreated();
            Check("数据目录可写", true, AppPaths.Root);
            Check("笔记目录存在", Directory.Exists(AppPaths.NotesDir), AppPaths.NotesDir);

            var probe = Path.Combine(AppPaths.Root, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            Check("目录读写正常", true);

            // 索引、设置、规则这三个文件在**全新安装**时都还不存在 ——
            // 自检刻意不写数据，所以不能断言"必须存在"，否则新用户跑一次自检
            // 会看到几个 ✗，以为程序坏了。改为：存在就要能读出内容，不存在也算正常。
            foreach (var (label, path) in new (string, string)[]
                     {
                         ("索引文件", AppPaths.IndexFile),
                         ("设置文件", AppPaths.SettingsFile),
                         ("分类规则文件", AppPaths.RulesFile),
                     })
            {
                if (!File.Exists(path))
                {
                    Check(label, true, $"{path}（首次运行尚未生成，属正常）");
                    continue;
                }

                try
                {
                    var text = File.ReadAllText(path, Encoding.UTF8);
                    Check(label, text.Trim().Length > 0, $"{path}（{text.Length} 字符）");
                }
                catch (Exception ex)
                {
                    Check(label, false, $"{path} 读取失败：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Check("数据目录可用", false, ex.Message);
        }

        // ---- 设置 ----
        Section("设置");
        try
        {
            var s = App.Settings.Settings;
            sb.AppendLine($"  上下班：{s.WorkStartHour:00}:{s.WorkStartMinute:00} — {s.WorkEndHour:00}:{s.WorkEndMinute:00}");
            sb.AppendLine($"  提前提醒：{s.RemindMinutesBefore} 分钟（{(s.ReminderEnabled ? "已启用" : "已关闭")}）");
            sb.AppendLine($"  悬浮窗：{(s.FloatingEnabled ? "开" : "关")}，缩放 {s.FloatingScale:0.00}，透明度 {s.FloatingOpacity:0.00}");
            sb.AppendLine($"  归档快照：{(s.SnapshotMode ? "开" : "关")}，全量哈希阈值 {s.FullHashLimitMb} MB");
            Check("设置读取正常", true);
        }
        catch (Exception ex)
        {
            Check("设置读取正常", false, ex.Message);
        }

        // ---- 图标与素材 ----
        Section("图标与素材");
        try
        {
            using var icon = IconAssets.LoadAppIcon();
            Check("托盘图标可加载", icon != null, $"{icon?.Width}x{icon?.Height}");

            var iconFile = IconAssets.EnsureIconFile();
            Check("可导出的 .ico 文件", !string.IsNullOrEmpty(iconFile), iconFile ?? "导出失败");

            var windowIcon = IconAssets.LoadWindowIcon();
            Check("窗口图标资源", windowIcon != null);

            var preview = IconAssets.LoadPreviewImage();
            Check("关于页图片资源", preview != null);

            // Image.Source 为 null 是合法的 —— 界面上只是一片空白，不会有任何报错。
            // 所以"能不能加载出来"必须单独断言，否则图丢了也没人知道。
            foreach (var entry in AssetCatalog.All.Where(e => e.Name != "icon"))
            {
                var image = IconAssets.Load(entry.Name);
                Check($"{entry.Files[0]} 可加载", image != null,
                    image is BitmapSource b
                        ? $"{b.PixelWidth}×{b.PixelHeight}"
                        : "读不出来 —— pack:// 资源或外部文件有问题");
            }

            // 实际生效的来源。刻意不等同于"磁盘上有没有外部文件"：
            // 文件存在但读不出来时，界面上用的是内置图，报告若还写"外部"就是误导。
            sb.AppendLine($"  素材目录：{AppPaths.AssetsDir}");
            sb.AppendLine($"  目录存在：{(Directory.Exists(AppPaths.AssetsDir) ? "是" : "否")}");

            var actual = IconAssets.LoadedFromExternal;
            foreach (var entry in AssetCatalog.All)
            {
                var label = entry.Files[0];
                var onDisk = AssetCatalog.FindExternal(entry.Name);

                if (actual.TryGetValue(entry.Name, out var used))
                    sb.AppendLine($"  {label,-20} 外部 {Path.GetFileName(used)}（生效）");
                else if (onDisk != null)
                    sb.AppendLine($"  {label,-20} 外部文件读不出来，已回落内置（{Path.GetFileName(onDisk)}）");
                else
                    sb.AppendLine($"  {label,-20} 内置默认");
            }
        }
        catch (Exception ex)
        {
            Check("图标加载", false, ex.Message);
        }

        // ---- 全局快捷键 ----
        Section("全局快捷键");
        try
        {
            var settings = App.Settings.Settings;
            var app = App.Instance;
            app.ApplyQuickCaptureHotKey();

            sb.AppendLine($"  配置的组合键：{settings.QuickCaptureHotkey}（{(settings.QuickCaptureEnabled ? "已启用" : "已关闭")}）");

            if (settings.QuickCaptureEnabled)
            {
                bool otherInstance = App.IsAnotherInstanceRunning();

                if (otherInstance)
                {
                    // 自检允许在程序运行时执行，这时快捷键抢不到是必然的，
                    // 不能当成故障报出去 —— 否则每次都会出现一条假警报。
                    sb.AppendLine("  · 检测到另一个鲸记实例正在运行，快捷键归它持有（正常）");
                    sb.AppendLine("    要看真实注册结果，请先退出正在运行的那个鲸记再跑自检。");
                }
                else
                {
                    Check("注册成功", app.HotKeys.HasAnyRegistration,
                        app.HotKeyError ?? HotKeyService.Format(
                            ParseMods(settings.QuickCaptureHotkey), ParseKey(settings.QuickCaptureHotkey)));
                }

                // 组合键解析的往返校验
                var ok = HotKeyService.TryParse(settings.QuickCaptureHotkey, out var mods, out var vk);
                Check("组合键可解析", ok, ok ? HotKeyService.Format(mods, vk) : "解析失败");
            }
            else
            {
                sb.AppendLine("  （已关闭，跳过）");
            }
        }
        catch (Exception ex)
        {
            Check("快捷键注册", false, ex.Message);
        }

        // ---- 桌面快捷方式 ----
        // 只在临时目录里试创建，**不动用户桌面上那个**。
        // 早期版本直接打桌面，于是"从解压出来的副本跑一次自检"就把快捷方式改指到临时目录，
        // 副本一删快捷方式就废了 —— 自检必须是无副作用的。
        Section("桌面快捷方式");
        try
        {
            var target = ShortcutService.ResolveLaunchTarget();
            sb.AppendLine($"  启动目标：{target.Target}");
            if (!string.IsNullOrEmpty(target.Arguments)) sb.AppendLine($"  启动参数：{target.Arguments}");
            sb.AppendLine($"  工作目录：{target.WorkingDirectory}");
            sb.AppendLine($"  桌面路径：{ShortcutService.DesktopDirectory}");

            var desktopLink = ShortcutService.ShortcutPath;
            if (File.Exists(desktopLink))
            {
                var pointsTo = ShortcutService.TryReadTarget(desktopLink);
                sb.AppendLine($"  桌面上已有快捷方式，指向：{pointsTo ?? "(读不出来)"}");
                if (!string.IsNullOrEmpty(pointsTo) && !File.Exists(pointsTo))
                    sb.AppendLine("  ⚠ 它指向的文件不存在了，建议在设置页里重新创建一次");
            }
            else
            {
                sb.AppendLine("  桌面上还没有快捷方式（设置页或托盘菜单里可以创建）");
            }

            var probeDir = Path.Combine(AppPaths.LogDir, "shortcut-probe");
            Directory.CreateDirectory(probeDir);
            var probeLink = Path.Combine(probeDir, ShortcutService.ShortcutName);

            var created = ShortcutService.TryCreateAt(probeLink, out var message);
            Check("能创建快捷方式（在临时目录里验证，不动桌面）", created, message);
            Check("创建出来的文件存在", File.Exists(probeLink), probeLink);

            var readBack = ShortcutService.TryReadTarget(probeLink);
            Check("能读回它的启动目标", !string.IsNullOrEmpty(readBack), readBack ?? "读不出来");
            Check("启动目标就是当前运行的 exe",
                !string.IsNullOrEmpty(readBack) &&
                string.Equals(readBack, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
                $"{readBack} / 当前 {Environment.ProcessPath}");

            try { File.Delete(probeLink); Directory.Delete(probeDir, true); }
            catch { /* 删不掉不影响结论 */ }
        }
        catch (Exception ex)
        {
            Check("创建快捷方式", false, ex.Message);
        }

        // ---- 开机自启 ----
        Section("开机自启");
        try
        {
            var registered = App.AutoStart.IsEnabled();
            Check("注册表状态与设置一致", registered == App.Settings.Settings.StartWithWindows,
                registered ? "已在注册表中" : "未注册");
        }
        catch (Exception ex)
        {
            Check("读取开机自启状态", false, ex.Message);
        }

        // ---- 日报 / 周报 ----
        Section("日报与周报");
        try
        {
            var today = DateTime.Today;
            var daily = ReportService.BuildDaily(App.Store.Book, today);
            Check("日报可生成", daily.Length > 0, $"{daily.Length} 字符");
            sb.AppendLine();
            sb.AppendLine("  ── 今日日报前几行 ──");
            foreach (var line in daily.Split('\n').Take(6))
                sb.AppendLine("  " + line.TrimEnd());

            var weekly = ReportService.BuildWeekly(App.Store.Book, today);
            Check("周报可生成", weekly.Length > 0,
                $"{weekly.Length} 字符，本周从 {ReportService.WeekStart(today):yyyy-MM-dd}（{ReportService.WeekdayText(ReportService.WeekStart(today))}）起");

            var wrote = ReportService.TryWriteDaily(App.Store.Book, today, out var file, out _);
            Check("日报可落盘", wrote, file);
        }
        catch (Exception ex)
        {
            Check("生成报告", false, ex.Message);
        }

        // ---- 窗口创建 ----
        // WPF 最容易踩的坑都在这里：资源键写错、把画刷当样式用、事件在解析阶段
        // 就被触发…… 这些问题编译期一个都不报。所以自检把每个窗口真的创建并显示一次。
        Section("窗口创建");
        CheckFn windowProbe = (n, ok, d) => Check(n, ok, d);
        ProbeWindow(sb, windowProbe, "主面板", () => new MainWindow());
        ProbeWindow(sb, windowProbe, "桌面小鲸鱼", () => new FloatingWindow(), parkFloating: true);
        ProbeWindow(sb, windowProbe, "快速记录", () => new QuickCaptureWindow());
        ProbeWindow(sb, windowProbe, "日报 / 周报", () =>
        {
            var w = new ReportWindow();
            w.Load("自检样张", "（不会真的显示）",
                "# 样例\n\n这是自检生成的样张，用来确认报告窗能正常渲染。", null, "自检");
            return w;
        });
        ProbeWindow(sb, windowProbe, "托盘菜单", () =>
        {
            var w = new TrayMenuWindow();
            w.AddItem("自检项", () => { });
            return w;
        });

        // ---- 页签互斥 ----
        // 三个页面是叠在同一个 Grid 单元格里的，只要有第二页被设成 Visible 就会完全重叠。
        // 这里按 ToggleButton 的真实行为（先翻转 IsChecked 再抛 Click）模拟点击，
        // 逐个页签验证"永远只有一页可见"。
        Section("页签互斥");
        ProbeNavExclusivity(Check);

        // ---- 文件筛选 ----
        // 和页签同一类 bug：筛选状态读的是「另一个」胶囊的 IsChecked，于是点
        // 「只看当天」时状态不翻转、界面毫无反应。这里逐个路径模拟点击。
        Section("文件筛选");
        ProbeFileFilter(Check);

        // ---- 日期分组 ----
        // 分组顺序取决于记录在集合里的出现顺序：排序写错的话栏头次序会乱，
        // 但每一栏自己看上去都是"正常"的。所以断言数量、次序和条数总和。
        Section("文件日期分组");
        ProbeFileDateGrouping(Check);

        // ---- 今日记录页的归档文件 ----
        // 需求就是两条：样式和文件归档页一致、多条堆叠时不重叠。
        // 两条都能断言：模板对象是不是同一个、卡片矩形有没有交叠。
        Section("今日记录里的归档文件");
        ProbeDayFileList(Check);

        // ---- 滑杆 ----
        // "滑块拖不动"这类问题编译期完全没有痕迹，而且界面上看不出是模板的哪一部分断了。
        // 这里直接把 Thumb 的拖拽事件抛出去，看 Slider 的值会不会跟着变。
        Section("滑杆");
        ProbeSliders(Check);

        // ---- 悬浮窗缩放 ----
        // 拖动滑杆最终要落到"小鲸鱼跟着变大变小且不被裁"。窗口尺寸变了但内容没跟着缩放时，
        // 界面上只是"有点挤"，量一下内容底边和窗口底边才知道是真被裁了。
        Section("悬浮窗缩放");
        ProbeFloatingScale(Check);

        // ---- 小鲸鱼动画 ----
        // 动画的失效方式全是**安静的**：素材没扫到、帧切错、画布换大了但显示框没跟着放大 ——
        // 表现都只是"小鲸鱼不太对"，日志里一个字都没有。这里自己造一份最小素材真跑一遍。
        Section("小鲸鱼动画");
        ProbeFloatingAnimations(Check, sb);

        // ---- 悬浮态形象 ----
        // 鼠标移上去换一张图。最容易错的是"移出要换回来"（处理函数里有个提前 return）
        // 和"悬浮期间动画播完该落回哪张"（落回本体就会闪一下睁眼）。
        Section("悬浮态形象（表情差分）");
        ProbeHoverImage(Check, sb);

        // ---- 形象包 ----
        // 新版溟月与原版鲸鱼娘并存、可一键切换，且"永远解析得到一个能用的形象"。
        // 切换没接上时界面上只是"看起来还是原来那只"，几乎没法归因，所以真换一遍。
        Section("形象包（新版 / 原版切换）");
        ProbeCharacterPacks(Check, sb);

        // 上面那节测的是"解析函数对不对"，这节测的是"设置页上那一排按钮点下去有没有用"。
        // 两回事：解析全对但按钮是灰的，用户看到的仍然是"点新形象没反应"。
        ProbeCharacterChipsPage(Check, sb);

        // ---- 透明视频 ----
        // 这条链路跨进程（ffmpeg 是外部程序），断了的表现是"点下去什么都不发生"。
        Section("透明视频动画");
        ProbeVideoPlayback(Check, sb);

        // 上面那节测的是"动画能不能播"，这节测的是"播出来的画面透不透明"。
        // 两回事：帧数全对、一个错都不报，但 alpha 全丢了，角色会顶着黑底显示。
        ProbeVideoAlpha(Check, sb);

        // ---- 月历面板 ----
        // 网格偏移、42 格、跨月边界这类算错的话，日历"看着正常"但日期整体错位，
        // 肉眼很难发现。这里把几何和事件都断言一遍。
        Section("月历面板");
        ProbeMonthCalendar(Check);
        ProbeJumpParser(Check);

        // ---- 数据统计 ----
        Section("数据统计");
        try
        {
            var book = App.Store.Book;
            sb.AppendLine($"  有记录的天数：{book.Days.Count}");
            sb.AppendLine($"  记录条目总数：{book.Days.Sum(d => d.Entries.Count)}");
            sb.AppendLine($"  归档文件总数：{book.Files.Count}");
            var missing = book.Files.Count(f => f.IsMissing);
            if (missing > 0) sb.AppendLine($"  其中已不在原位置：{missing}");
        }
        catch (Exception ex)
        {
            Check("统计数据", false, ex.Message);
        }

        sb.AppendLine();
        sb.AppendLine(new string('-', 60));
        sb.AppendLine(failures == 0 ? "结论：全部通过" : $"结论：{failures} 项未通过");

        return sb.ToString();
    }

    /// <summary>
    /// 模拟点击三个页签，验证任意时刻都只有一页可见。
    ///
    /// 曾经真实出过的 bug：页面可见性各读各的 ToggleButton.IsChecked，
    /// 而 ToggleButton 之间不互斥 —— 点「文件归档」后「今日记录」仍是勾选状态，
    /// 两页同时 Visible，而三个页面叠在同一个 Grid 单元格里，表现就是内容完全重叠。
    /// </summary>
    private static void ProbeNavExclusivity(CheckFn check)
    {
        MainWindow? window = null;
        try
        {
            window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Left = -32000,
                Top = -32000,
            };
            window.Show();
            window.UpdateLayout();

            var notes = window.FindName("NavNotes") as ToggleButton;
            var files = window.FindName("NavFiles") as ToggleButton;
            var settings = window.FindName("NavSettings") as ToggleButton;

            if (notes == null || files == null || settings == null)
            {
                check("找到三个页签按钮", false, "FindName 返回空");
                return;
            }

            // 还原 ToggleButton 的真实点击行为：先翻转 IsChecked，再抛出 Click
            void ClickNav(ToggleButton button)
            {
                button.IsChecked = !(button.IsChecked ?? false);
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }

            ClickNav(files);
            check("切到「文件归档」后只有一页可见", window.VisiblePageCount == 1, window.NavStateReport);
            check("当前页确实是「文件归档」", window.ActivePage == NavPage.Files, window.ActivePage.ToString());

            ClickNav(settings);
            check("切到「设置」后只有一页可见", window.VisiblePageCount == 1, window.NavStateReport);
            check("当前页确实是「设置」", window.ActivePage == NavPage.Settings, window.ActivePage.ToString());

            ClickNav(notes);
            check("切回「今日记录」后只有一页可见", window.VisiblePageCount == 1, window.NavStateReport);

            // 再点一次当前页签：IsChecked 被反选掉，此时必须维持当前页，不能三页全隐藏
            ClickNav(notes);
            check("重复点当前页签不会出现空白面板",
                window.VisiblePageCount == 1 && window.ActivePage == NavPage.Notes,
                window.NavStateReport);
        }
        catch (Exception ex)
        {
            check("页签互斥", false, ex.Message);
        }
        finally
        {
            try { window?.Close(); }
            catch { /* 关不掉不影响结论 */ }
        }
    }

    /// <summary>
    /// 文件归档页「只看当天 / 全部」的交互断言。
    ///
    /// 曾经真实出过的 bug：OnFileFilterClick 里写的是
    /// <c>_showAllFiles = FileFilterAll.IsChecked == true;</c>
    /// —— 读的是「另一个」胶囊的状态。点「只看当天」时 FileFilterAll 还留着
    /// 上次的勾选，于是筛选条件根本没变，界面看上去就是"点了没反应"。
    /// </summary>
    private static void ProbeFileFilter(CheckFn check)
    {
        MainWindow? window = null;
        try
        {
            window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Left = -32000,
                Top = -32000,
            };
            window.Show();
            window.UpdateLayout();

            var today = window.FindName("FileFilterToday") as ToggleButton;
            var all = window.FindName("FileFilterAll") as ToggleButton;

            if (today == null || all == null)
            {
                check("找到两个筛选胶囊", false, "FindName 返回空");
                return;
            }

            // 还原 ToggleButton 的真实点击行为：先翻转 IsChecked，再抛出 Click
            void ClickFilter(ToggleButton button)
            {
                button.IsChecked = !(button.IsChecked ?? false);
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }

            bool ExactlyOneChecked() => (today.IsChecked == true) ^ (all.IsChecked == true);

            check("初始为「只看当天」",
                !window.ShowAllFiles && today.IsChecked == true && all.IsChecked == false,
                window.FileFilterStateReport);

            // 该显示多少条，直接拿存储层算出来对照，顺带验证列表真的刷新了
            var day = DateTime.Today.ToString("yyyy-MM-dd");
            int expectToday = App.Store.QueryFiles(day).Count();
            int expectAll = App.Store.QueryFiles(null).Count();
            check("默认显示当天文件数",
                window.VisibleFileCount == expectToday,
                $"显示 {window.VisibleFileCount}，当天 {expectToday}，全部 {expectAll}");

            ClickFilter(all);
            check("点「全部」切到全部范围",
                window.ShowAllFiles && all.IsChecked == true && today.IsChecked == false,
                window.FileFilterStateReport);
            check("切到全部后文件数跟着变",
                window.VisibleFileCount == expectAll, $"显示 {window.VisibleFileCount}，全部 {expectAll}");

            // ★ 报障路径：从「全部」点回「只看当天」
            ClickFilter(today);
            check("点「只看当天」能切回当天范围",
                !window.ShowAllFiles && today.IsChecked == true && all.IsChecked == false,
                window.FileFilterStateReport);
            check("切回当天后文件数跟着变",
                window.VisibleFileCount == expectToday, $"显示 {window.VisibleFileCount}，当天 {expectToday}");

            // 再点一次当前胶囊：IsChecked 被反选掉，此时必须维持当前范围，不能两个都不选
            ClickFilter(today);
            check("重复点当前胶囊维持范围且恰好选中一个",
                !window.ShowAllFiles && ExactlyOneChecked(), window.FileFilterStateReport);

            // 来回多切几次，确认状态不会漂
            for (int i = 0; i < 3; i++)
            {
                ClickFilter(all);
                ClickFilter(today);
            }
            check("反复切换后仍是当天范围且只有一个被选中",
                !window.ShowAllFiles && ExactlyOneChecked() && window.VisibleFileCount == expectToday,
                window.FileFilterStateReport);

            // 胶囊文案应当说明"当天"而不是"今天"——它跟着当前查看的日期走
            check("胶囊文案为「只看当天」",
                today.Content as string == "只看当天",
                today.Content as string ?? "（空）");

            // ---- 目标日期必须写在拖拽横幅上 ----
            // 拖入的文件归属"当前查看的那一天"。这个规则猜不到：在看 9/7 时拖文件会被记到 9/7，
            // 回到今天找不到，看上去就像"只看当天坏了"。所以横幅必须显式写出目标日期。
            var past = DateTime.Today.AddDays(-3);
            var bannerTitle = window.FindName("DropBannerTitle") as TextBlock;
            var bannerHint = window.FindName("DropBannerHint") as TextBlock;

            if (bannerTitle == null || bannerHint == null)
            {
                check("找到拖拽横幅", false, "FindName 返回空");
                return;
            }

            check("今天视图的横幅说的是今天",
                bannerTitle.Text.Contains("今天", StringComparison.Ordinal),
                bannerTitle.Text);

            window.GoToDate(past);
            window.UpdateLayout();
            check("查看历史日期时横幅写明目标日期",
                bannerTitle.Text.Contains($"{past:M月d日}", StringComparison.Ordinal) &&
                bannerHint.Text.Contains("不是今天", StringComparison.Ordinal),
                $"{bannerTitle.Text} / {bannerHint.Text}");

            check("切到历史日期后「只看当天」跟着变成那一天的",
                !window.ShowAllFiles &&
                window.VisibleFileCount == App.Store.QueryFiles(past.ToString("yyyy-MM-dd")).Count(),
                $"范围报告：{window.FileFilterStateReport}，显示 {window.VisibleFileCount}");

            window.GoToDate(DateTime.Today);
            check("回到今天后横幅恢复为今天",
                bannerTitle.Text.Contains("今天", StringComparison.Ordinal) &&
                !bannerTitle.Text.Contains("记入", StringComparison.Ordinal),
                bannerTitle.Text);
        }
        catch (Exception ex)
        {
            check("文件筛选", false, ex.Message);
        }
        finally
        {
            try { window?.Close(); }
            catch { /* 关不掉不影响结论 */ }
        }
    }

    /// <summary>
    /// 把主面板的某一页渲染成 PNG。
    ///
    /// 自检能断言"有几个栏头""卡片有没有叠住"，但断言不了"看起来对不对"—— 文字被裁掉、
    /// 颜色在某个主题下糊掉、对齐歪了，这些都只能靠眼睛。渲染用离屏窗口，不会弹到用户面前。
    /// </summary>
    public static string WritePageShot(string path, NavPage page)
    {
        MainWindow? window = null;
        try
        {
            window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Left = -32000,
                Top = -32000,
            };

            window.Show();
            window.SwitchTo(page);
            if (page == NavPage.Files) window.SetFileFilter(true);   // 归档页用「全部」，能看到跨天分组
            window.UpdateLayout();

            int w = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
            int h = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var fs = File.Create(path);
            encoder.Save(fs);
            return path;
        }
        finally
        {
            try { window?.Close(); }
            catch { /* 关不掉不影响截图 */ }
        }
    }

    /// <summary>
    /// 设置页两个滑杆能不能真的拖。
    ///
    /// Slider 的拖动链路是 Thumb.DragDelta → Slider 按鼠标在 Track 上的位置换算 Value。
    /// 只要其中一环断了（Track 宽度为 0、Thumb 被别的元素盖住、模板里没有 PART_Track），
    /// 表现都是"拖动没反应"，而从界面上完全看不出是哪一环。所以这里逐环断言。
    /// </summary>
    private static void ProbeSliders(CheckFn check)
    {
        MainWindow? window = null;
        try
        {
            window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Left = -32000,
                Top = -32000,
            };
            window.Show();
            window.SwitchTo(NavPage.Settings);   // Collapsed 时不会走布局，量不到尺寸
            window.UpdateLayout();

            foreach (var name in new[] { "ScaleSlider", "OpacitySlider" })
            {
                if (window.FindName(name) is not Slider slider)
                {
                    check($"找到 {name}", false, "FindName 返回空");
                    continue;
                }

                bool isScale = ReferenceEquals(slider, window.FindName("ScaleSlider"));

                check($"{name} 可交互",
                    slider.IsEnabled && slider.IsHitTestVisible && slider.ActualWidth > 0,
                    $"IsEnabled={slider.IsEnabled} 可见={slider.IsHitTestVisible} 宽={slider.ActualWidth:0}");

                // 滑杆范围和设置校验必须是一组常量：两边各写一份的话，
                // 手改过 settings.json 就会出现"滑杆显示一个数、实际用另一个数"
                double expectMin = isScale ? AppSettings.MinFloatingScale : AppSettings.MinFloatingOpacity;
                double expectMax = isScale ? AppSettings.MaxFloatingScale : AppSettings.MaxFloatingOpacity;
                check($"{name} 的取值范围和设置校验一致",
                    Math.Abs(slider.Minimum - expectMin) < 1e-9 &&
                    Math.Abs(slider.Maximum - expectMax) < 1e-9,
                    $"滑杆 {slider.Minimum:0.##}–{slider.Maximum:0.##}，设置 {expectMin:0.##}–{expectMax:0.##}");

                var track = Descendants(slider).OfType<Track>().FirstOrDefault();
                check($"{name} 模板里有可用的 PART_Track",
                    track != null && track.ActualWidth > 0 && track.ActualHeight > 0,
                    track == null
                        ? "可视树里找不到 Track"
                        : $"Track {track.ActualWidth:0}×{track.ActualHeight:0}");

                var thumb = Descendants(slider).OfType<Thumb>().FirstOrDefault();
                check($"{name} 的拖动手柄有实际大小",
                    thumb != null && thumb.ActualWidth > 0 && thumb.ActualHeight > 0,
                    thumb == null
                        ? "可视树里找不到 Thumb"
                        : $"Thumb {thumb.ActualWidth:0}×{thumb.ActualHeight:0}");

                if (thumb == null) continue;

                // 命中测试：手柄中心最上层的应该就是手柄本身，被别的元素盖住就拖不动
                var center = thumb.TranslatePoint(
                    new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2), window);
                var hit = window.InputHitTest(center) as DependencyObject;
                bool hitIsThumb = hit != null &&
                    (ReferenceEquals(hit, thumb) || IsDescendantOf(hit, thumb));
                check($"{name} 的手柄没有被别的元素挡住",
                    hitIsThumb,
                    $"命中到 {hit?.GetType().Name ?? "（空）"}{(hit is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name) ? $"({fe.Name})" : "")}");

                // 真的抛一次 DragDelta，看值会不会跟着走 —— 这是拖动链路的实测
                double before = slider.Value;
                var label = window.FindName(isScale ? "ScaleText" : "OpacityText") as TextBlock;
                string labelBefore = label?.Text ?? "";

                double target = before < slider.Maximum - 0.01 ? slider.Maximum : slider.Minimum;
                double direction = target > before ? 1 : -1;

                thumb.RaiseEvent(new DragDeltaEventArgs(direction * 30, 0)
                {
                    RoutedEvent = Thumb.DragDeltaEvent,
                });

                check($"{name} 拖动后值会变",
                    Math.Abs(slider.Value - before) > 1e-9,
                    $"{before:0.###} → {slider.Value:0.###}");

                // 值变了还不够：界面上那个数字必须跟着变，否则就是"拖得动但看着没反应"
                check($"{name} 拖动后读数跟着更新",
                    label != null && label.Text.Length > 0 && label.Text != labelBefore,
                    $"「{labelBefore}」→「{label?.Text}」");

                check($"{name} 拖动后 _syncing 守卫已释放",
                    !window.IsSyncingGuardSet,
                    window.IsSyncingGuardSet ? "守卫按着不放，后续所有设置改动都会被静默吞掉" : "已释放");

                // 还原，别把拖动留下的值带给后面的自检
                slider.Value = before;
                check($"{name} 还原后读数回到原值",
                    label == null || label.Text == labelBefore,
                    $"值 {slider.Value:0.###}，读数「{label?.Text}」");
            }
        }
        catch (Exception ex)
        {
            check("滑杆", false, ex.Message);
        }
        finally
        {
            // 模拟拖动会触发 700ms 的保存节流：如果它拖到自检结束之后才走到，
            // 就会把探针的值写进用户配置。收尾先掐掉。
            window?.CancelPendingSaves();

            try { window?.Close(); }
            catch { /* 关不掉不影响结论 */ }
        }
    }

    /// <summary>child 是否在 ancestor 的子树里。</summary>
    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        var node = child;
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>
    /// 悬浮窗的"大小"设置到底有没有落到画面上。
    ///
    /// 真实出过的问题：改的是窗口宽高，内容（鲸鱼 168px、状态条）却是固定尺寸 ——
    /// 放大时窗口大了内容没大（一片空），缩小时内容撑不下就被裁。
    /// 所以这里逐档断言：内容底边不能越过窗口底边，鲸鱼也要跟着缩放。
    /// </summary>
    private static void ProbeFloatingScale(CheckFn check)
    {
        FloatingWindow? window = null;
        var settings = App.Settings.Settings;
        // 探针内部先各自还原一份，方便同一次自检里的后续探针读到原值；
        // 万一这里漏了哪个字段，RunReport 外面那层整体快照还会兜住（不会写进用户配置）。
        double scaleBackup = settings.FloatingScale;
        double opacityBackup = settings.FloatingOpacity;

        try
        {
            // 必须先把"不用时收起"关掉：开着的话窗口一加载就把自己收成小胶囊，
            // 后面量到的宽高全是收起态的（48px），和展开态的期望值对不上 ——
            // 用默认设置（autoCollapse 默认 true）跑就会一片红。
            settings.FloatingAutoCollapse = false;

            // 探针窗口不能出现在用户桌面上：RestorePosition 是按设置里的位置摆放的，
            // 构造函数里传的 -32000 会被它覆盖掉 —— 那样每次跑自检，用户的桌面上就
            // 会闪一下悬浮窗。先把设置里的位置临时挪到屏幕外，跑完由外层快照还原。
            settings.FloatingLeft = Offscreen;
            settings.FloatingTop = Offscreen;

            window = new FloatingWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,          // 不要抢用户当前的焦点窗口
                BackgroundProbeMode = true,     // 别把窗口拉回屏幕内
                Left = Offscreen,
                Top = Offscreen,
            };
            window.Show();
            window.UpdateLayout();

            check("探针窗口落在屏幕外（不会闪到用户的桌面）",
                window.Left <= Offscreen + 1 && window.Top <= Offscreen + 1,
                $"窗口位置 ({window.Left:0.#}, {window.Top:0.#})");

            var whaleImage = Descendants(window).OfType<Image>()
                .FirstOrDefault(i => i.Name == "WhaleImage");

            if (whaleImage == null)
            {
                check("找到鲸鱼图", false, "可视树里没有 WhaleImage");
                return;
            }

            double? heightAt1 = null;

            foreach (double scale in new[] { 0.7, 1.0, 1.6 })
            {
                window.ApplyScaleAndOpacity(scale, 1.0);
                window.UpdateLayout();

                var (whaleTop, contentBottom, windowHeight) = window.MeasureLayout();

                check($"缩放 {scale:0.0} 时窗口尺寸跟着变",
                    Math.Abs(window.Width - Math.Round(ExpandedW * scale)) < 1.5,
                    $"窗口宽 {window.Width:0}，期望 {Math.Round(ExpandedW * scale):0}");

                check($"缩放 {scale:0.0} 时内容不被裁切",
                    contentBottom <= windowHeight + 0.5 && whaleTop >= -0.5,
                    $"内容底边 {contentBottom:0.#} / 窗口高 {windowHeight:0.#}" +
                    (contentBottom > windowHeight ? $"（超出 {contentBottom - windowHeight:0.#}px）" : ""));

                if (Math.Abs(scale - 1.0) < 1e-9)
                {
                    heightAt1 = RenderedHeight(whaleImage, window);
                }
                else if (heightAt1.HasValue)
                {
                    double rendered = RenderedHeight(whaleImage, window);
                    double expected = heightAt1.Value * scale;
                    check($"缩放 {scale:0.0} 时鲸鱼等比跟着变",
                        Math.Abs(rendered - expected) < 1.5,
                        $"鲸鱼渲染高 {rendered:0.#}，期望 {expected:0.#}");
                }
            }

            // 透明度要落到窗口上
            window.ApplyScaleAndOpacity(1.0, 0.55);
            window.UpdateLayout();
            check("透明度设置生效", Math.Abs(window.Opacity - 0.55) < 1e-6, $"实际 {window.Opacity:0.##}");

            window.ApplyScaleAndOpacity(1.0, 1.0);
            check("透明度可以调回 1.0", Math.Abs(window.Opacity - 1.0) < 1e-6, $"实际 {window.Opacity:0.##}");

            // 拖动滑杆时的实时预览：要立刻看到变化，但不能顺手把设置写掉
            window.ApplyScaleAndOpacity(1.0, 1.0);
            window.UpdateLayout();
            window.PreviewScaleAndOpacity(1.4, 0.8);
            window.UpdateLayout();

            check("拖动预览立刻改变窗口尺寸",
                Math.Abs(window.Width - Math.Round(ExpandedW * 1.4)) < 1.5,
                $"窗口宽 {window.Width:0}，期望 {Math.Round(ExpandedW * 1.4):0}");

            check("拖动预览同时改透明度",
                Math.Abs(window.Opacity - 0.8) < 1e-6, $"实际 {window.Opacity:0.##}");

            check("拖动预览不写入设置（落盘仍由节流负责）",
                Math.Abs(settings.FloatingScale - 1.0) < 1e-9 && Math.Abs(settings.FloatingOpacity - 1.0) < 1e-9,
                $"设置里仍是 {settings.FloatingScale:0.##} / {settings.FloatingOpacity:0.##}");
        }
        catch (Exception ex)
        {
            check("悬浮窗缩放", false, ex.Message);
        }
        finally
        {
            settings.FloatingScale = scaleBackup;
            settings.FloatingOpacity = opacityBackup;
            try
            {
                window?.ApplyScaleAndOpacity(scaleBackup, opacityBackup);
                window?.Close();
            }
            catch { /* 关不掉不影响结论 */ }
        }
    }

    /// <summary>
    /// 把悬浮窗在最小 / 默认 / 最大三档下渲染成一张 PNG，横向排开。
    ///
    /// 用离屏窗口渲染而不是抓屏：抓屏会受多显示器（可能有负坐标）和 DPI 影响，
    /// 量出来的尺寸对不上窗口真实尺寸，反而误导。离屏渲染是确定性的。
    /// </summary>
    public static string WriteFloatingShot(string path)
    {
        // 同一个道理：截图要临时改缩放/不透明度和"自动收起"，跑完必须整体还原，
        // 否则退出时 Save() 会把临时的 1.0 不透明度写进用户配置。
        var snapshot = App.Settings.Snapshot();
        try
        {
            return WriteFloatingShotCore(path);
        }
        finally
        {
            App.Settings.Restore(snapshot);
        }
    }

    private static string WriteFloatingShotCore(string path)
    {
        var settings = App.Settings.Settings;
        double autoCollapseBackup = settings.FloatingAutoCollapse ? 1 : 0;

        var shots = new List<(System.Windows.Media.Imaging.BitmapSource Bmp, int W, int H)>();

        try
        {
            // 截图要的是展开态，别让"不用时收起"把窗口缩成小胶囊
            settings.FloatingAutoCollapse = false;

            // 窗口一律摆到屏幕外：RestorePosition 会按设置里的位置摆放，不挪开的话
            // 每次出图都会在用户桌面上闪三个悬浮窗。
            settings.FloatingLeft = Offscreen;
            settings.FloatingTop = Offscreen;

            foreach (double scale in new[] { AppSettings.MinFloatingScale, 1.0, AppSettings.MaxFloatingScale })
            {
                var window = new FloatingWindow
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false,
                    ShowActivated = false,          // 不要抢用户当前的焦点窗口
                    BackgroundProbeMode = true,     // 别把窗口拉回屏幕内
                    Left = Offscreen,
                    Top = Offscreen,
                };

                try
                {
                    window.Show();
                    window.ApplyScaleAndOpacity(scale, 1.0);
                    window.UpdateLayout();

                    int w = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
                    int h = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));

                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bmp.Render(window);
                    bmp.Freeze();

                    shots.Add((bmp, w, h));
                }
                finally
                {
                    try { window.Close(); }
                    catch { /* 关不掉不影响截图 */ }
                }
            }

            const int gap = 18;
            int totalW = shots.Sum(s => s.W) + gap * (shots.Count - 1);
            int totalH = shots.Count == 0 ? 1 : shots.Max(s => s.H);

            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // 悬浮窗背景是透明的，垫一层窗口底色，否则看图的人分不清边界
                var bg = Application.Current?.TryFindResource("WindowBackgroundBrush")
                             as System.Windows.Media.Brush
                         ?? System.Windows.Media.Brushes.Black;
                dc.DrawRectangle(bg, null, new Rect(0, 0, totalW, totalH));

                double x = 0;
                foreach (var (bmp, w, h) in shots)
                {
                    dc.DrawImage(bmp, new Rect(x, 0, w, h));
                    x += w + gap;
                }
            }

            var composed = new System.Windows.Media.Imaging.RenderTargetBitmap(
                totalW, totalH, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            composed.Render(visual);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(composed));

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var fs = File.Create(path);
            encoder.Save(fs);
            return path;
        }
        finally
        {
            // 只有"自动收起"要在这儿还原 —— 它影响的是这三张图拍得对不对；
            // 缩放和不透明度（以及一切别的东西）由外面那层整体快照兜底。
            settings.FloatingAutoCollapse = autoCollapseBackup == 1;
        }
    }

    /// <summary>
    /// 元素在窗口坐标里的实际渲染高度。
    ///
    /// 不能读 ActualHeight —— LayoutTransform 作用在元素外面，ActualHeight 仍然是变换前的
    /// 布局尺寸（鲸鱼永远报 168），量不出"有没有跟着放大"。
    /// </summary>
    private static double RenderedHeight(FrameworkElement element, UIElement root)
        => element.TranslatePoint(new Point(0, element.ActualHeight), root).Y
         - element.TranslatePoint(new Point(0, 0), root).Y;

    /// <summary>
    /// 小鲸鱼的动态素材（精灵图动画）。
    ///
    /// 探针自己造一份最小可用的素材（**真的 PNG + 真的帧表**）放进临时素材目录，
    /// 把 <c>WHALELOG_ASSET_DIR</c> 指过去，再按真实方式创建悬浮窗。
    /// 验完立刻还原环境变量、删掉临时目录 —— 绝不碰用户自己的素材，
    /// 也绝不让临时素材留在用户界面里（这正是"探针必须零副作用"那条教训）。
    /// </summary>
    private static void ProbeFloatingAnimations(CheckFn check, StringBuilder sb)
    {
        // 先用当前的真实素材目录报告一遍：用户报"我放了动画没反应"时，
        // 这份描述直接说明程序看了哪里、认出了什么、哪一份没认出来、为什么。
        var live = AnimLibrary.Scan();
        sb.AppendLine(Indent(live.Describe()));

        var probeRoot = Path.Combine(AppPaths.LogDir, "anim-probe");
        var assetDir = Path.Combine(probeRoot, "Assets");

        // 帧表里的数字刻意和样例一致：帧高 720、角色 600 → 显示框应当是 198。
        // 对不上就说明换算写错了，而这在界面上只表现为"鲸鱼变大了一点点"。
        const int frameSize = 720;
        const int cols = 2;
        const int count = 2;

        string? previousAssetDir = null;
        FloatingWindow? window = null;
        var settings = App.Settings.Settings;

        try
        {
            previousAssetDir = Environment.GetEnvironmentVariable("WHALELOG_ASSET_DIR");

            // 和"悬浮窗缩放"那段同一个道理：探针窗口不能出现在用户的桌面上。
            // RestorePosition 是按设置里的位置摆的，所以先把位置临时挪到屏幕外；
            // 不关掉"不用时收起"的话，窗口一加载就把自己收成小胶囊，
            // 而 Collapse() 会顺手 SavePosition() 把位置**写回 settings.json** ——
            // 探针就这么把自己的坐标落进用户配置了。跑完由外层快照整体还原。
            settings.FloatingAutoCollapse = false;
            settings.FloatingLeft = Offscreen;
            settings.FloatingTop = Offscreen;

            WriteProbeAnimation(assetDir, AnimActions.Click, frameSize, cols, count);

            // 扫描结果先单独断言一遍，免得后面界面出的问题被赖到扫描上
            var probeLib = AnimLibrary.Scan(assetDir);
            check("探针素材能被扫到", probeLib.HasAny && probeLib.Problems.Count == 0,
                probeLib.Problems.Count == 0
                    ? $"动作 {string.Join("、", probeLib.ActionNames)}"
                    : string.Join(" / ", probeLib.Problems));

            // ---- 让悬浮窗真的按这份素材加载 ----
            Environment.SetEnvironmentVariable("WHALELOG_ASSET_DIR", assetDir);

            window = new FloatingWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                BackgroundProbeMode = true,
                Left = Offscreen,
                Top = Offscreen,
            };
            window.Show();
            window.UpdateLayout();

            check("探针窗口落在屏幕外（不会闪到用户的桌面）",
                window.Left <= Offscreen + 1 && window.Top <= Offscreen + 1,
                $"窗口位置 ({window.Left:0.#}, {window.Top:0.#})");

            check("悬浮窗启用了动态素材", window.AnimationsOn, window.AnimStateReport);

            // 换素材最容易砸的一处：画布从 610 变成 720，显示框必须跟着放大，
            // 否则角色在屏幕上凭空小 15%（界面上只表现为"好像小了点"）
            check("鲸鱼框按帧表放大到约 198px",
                Math.Abs(window.WhaleHeight - 198) < 1.5,
                $"实际 {window.WhaleHeight:0.#}px（静态形象是 168px）");

            check("窗口高度跟着鲸鱼框一起变（两者必须出自同一个系数）",
                Math.Abs(window.Height - 326) < 1.5,
                $"实际 {window.Height:0.#}，期望 326（296 + 30）");

            var whale = Descendants(window).OfType<Image>().FirstOrDefault(i => i.Name == "WhaleImage");
            check("找到鲸鱼图", whale != null);
            if (whale == null) return;

            check("静止时显示的是与精灵图同画布的静态本体",
                whale.Source is not System.Windows.Media.Imaging.CroppedBitmap,
                whale.Source?.GetType().Name ?? "（空）");

            // ---- 播放链路 ----
            var player = window.AnimPlayer;
            check("播放器已建立", player != null && player.RegisteredCount == 1,
                $"登记动作 {player?.RegisteredCount ?? 0} 个");

            if (player == null) return;

            check("点一下鲸鱼能播起来（动作名走别名 click）",
                player.Play("click") && player.IsPlaying,
                $"动作={player.CurrentAction ?? "（无）"} 帧={player.CurrentFrame}");

            check("刚播起来就是第 0 帧（不能等下一个渲染 tick 才显示）",
                player.CurrentFrame == 0, $"实际第 {player.CurrentFrame} 帧");

            check("帧真的被切出来贴到图上了",
                whale.Source is System.Windows.Media.Imaging.CroppedBitmap,
                whale.Source?.GetType().Name ?? "（空）");

            // 确定性推进：不依赖显示器刷新率，直接问"时间过了这么久，该显示第几帧"
            player.AdvanceBy(TimeSpan.FromMilliseconds(120));   // 10fps → 120ms 跨过 1 帧

            check("时间往前走，帧号跟着走",
                player.CurrentFrame == 1, $"实际第 {player.CurrentFrame} 帧");

            player.AdvanceBy(TimeSpan.FromMilliseconds(500));

            check("播完自动停下（loop=false）", !player.IsPlaying && player.CurrentFrame == -1,
                $"播放中={player.IsPlaying} 帧={player.CurrentFrame}");

            check("播完回到静态本体（不留最后一帧在脸上）",
                whale.Source is not System.Windows.Media.Imaging.CroppedBitmap,
                whale.Source?.GetType().Name ?? "（空）");

            // ---- 关掉开关要真的回到静态 ----
            App.Settings.Settings.FloatingAnimations = false;
            window.ApplySettings();
            window.UpdateLayout();

            check("关掉「小鲸鱼动画」后退回静态形象",
                !window.AnimationsOn && Math.Abs(window.WhaleHeight - 168) < 0.5,
                $"启用={window.AnimationsOn} 鲸鱼框={window.WhaleHeight:0.#}px");

            check("退回静态后窗口高度也回到 296",
                Math.Abs(window.Height - 296) < 1.5, $"实际 {window.Height:0.#}");

            App.Settings.Settings.FloatingAnimations = true;
            window.ApplySettings();
            window.UpdateLayout();

            check("重新打开开关能立刻恢复动画",
                window.AnimationsOn && Math.Abs(window.WhaleHeight - 198) < 1.5,
                $"启用={window.AnimationsOn} 鲸鱼框={window.WhaleHeight:0.#}px");
        }
        catch (Exception ex)
        {
            check("小鲸鱼动画", false, ex.Message);
            sb.AppendLine("      " + ex);
        }
        finally
        {
            // 顺序很重要：先把窗口关掉（它可能还挂着播放器），再还原环境变量，
            // 最后才清缓存 —— 反过来的话，界面上会短暂地留着探针素材。
            try { window?.Close(); }
            catch { /* 关不掉不影响结论 */ }

            try
            {
                Environment.SetEnvironmentVariable("WHALELOG_ASSET_DIR", previousAssetDir);
                IconAssets.Reload();
            }
            catch { /* 还原失败也只能继续 */ }

            try { Directory.Delete(probeRoot, true); }
            catch { /* 删不掉不影响结论 */ }
        }
    }

    /// <summary>
    /// 悬浮态形象（表情差分）：鼠标停在悬浮窗上时整张换成另一张图。
    ///
    /// 看着简单，但有两处极易做错，这里各用一条断言钉住：
    ///
    ///   1. **移出必须换回本体。** <c>OnMouseLeave</c> 里"要不要自动收起"那个分支会提前
    ///      return，形象还原要是排在它后面，关掉自动收起的用户鼠标一移开就永远停在闭眼状态。
    ///      探针走的是**真实的处理函数**（而不是绕到内部方法），所以这条断言是有效的。
    ///   2. **悬浮期间动画播完，要落回悬浮那张。** 播放器播完落回的是 BaseImage，
    ///      它必须跟着悬浮状态一起切，否则会闪一下本体。
    ///
    /// 同样自己造素材（蓝底本体 + 红底悬浮图 + 一份能播的动画），把
    /// <c>WHALELOG_ASSET_DIR</c> 指过去，跑完还原环境变量并删掉临时目录 ——
    /// 绝不碰用户自己的素材，也不让探针素材留在界面上。
    /// </summary>
    private static void ProbeHoverImage(CheckFn check, StringBuilder sb)
    {
        // 先报告用户这边实际生效的是哪一张："我换了图没反应"时这一行最有用
        sb.AppendLine(Indent(AssetCatalog.Describe("whale-hover")));

        var probeRoot = Path.Combine(AppPaths.LogDir, "hover-probe");
        var assetDir = Path.Combine(probeRoot, "Assets");

        string? previousAssetDir = null;
        FloatingWindow? window = null;
        var settings = App.Settings.Settings;

        static string Fmt((int Width, int Height)? size)
            => size.HasValue ? $"{size.Value.Width}×{size.Value.Height}" : "（没取到）";

        try
        {
            previousAssetDir = Environment.GetEnvironmentVariable("WHALELOG_ASSET_DIR");

            // 关掉自动收起，才走得到 OnMouseLeave 里那个提前 return 的分支 ——
            // 那正是最容易把"形象还原"漏掉的一条路
            settings.FloatingAutoCollapse = false;
            settings.FloatingLeft = Offscreen;
            settings.FloatingTop = Offscreen;
            settings.FloatingAnimations = true;      // 悬浮与动画的配合要一起验

            Directory.CreateDirectory(assetDir);

            // 本体蓝、悬浮红：日志和出图里一眼看出此刻贴的是哪一张
            WriteProbeSolid(Path.Combine(assetDir, "whale.png"), 200, 200, 0, 0, 255);
            WriteProbeSolid(Path.Combine(assetDir, "whale-hover.png"), 200, 200, 255, 0, 0);

            // 放一份能播的动画：验"悬浮期间播完该落回哪张"要用到播放器
            WriteProbeAnimation(assetDir, AnimActions.Click, 720, 2, 2);

            Environment.SetEnvironmentVariable("WHALELOG_ASSET_DIR", assetDir);

            window = new FloatingWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                BackgroundProbeMode = true,
                Left = Offscreen,
                Top = Offscreen,
            };
            window.Show();
            window.UpdateLayout();

            check("探针窗口落在屏幕外（不会闪到用户的桌面）",
                window.Left <= Offscreen + 1 && window.Top <= Offscreen + 1,
                $"窗口位置 ({window.Left:0.#}, {window.Top:0.#})");

            check("悬浮形象已配上（没配时悬浮毫无变化，而不是报错）",
                window.HasHoverImage, AssetCatalog.Describe("whale-hover"));

            // 两张图必须同画布：不同的话鼠标一移上去角色就跳一下，
            // 而这在界面上只表现为"好像抖了一下"，几乎没法归因
            check("本体与悬浮形象同画布（不同就会在悬浮瞬间跳一下）",
                window.StillImageSize.HasValue && window.StillImageSize == window.HoverImageSize,
                $"本体 {FormatSize(window.StillImageSize)}，悬浮 {Fmt(window.HoverImageSize)}");

            check("还没悬浮时贴的是本体", window.ShowingStillImage,
                $"配上悬浮图={window.HasHoverImage}");

            window.SimulateMouseEnter();
            window.UpdateLayout();

            check("鼠标移入换成悬浮形象", window.ShowingHoverImage, "应当贴的是悬浮那张");

            // 播放期间不能抢 Source，否则动作会被截断成半张
            var player = window.AnimPlayer;
            if (player != null && player.Play(AnimActions.Click))
            {
                check("悬浮期间点一下照常播（切换形象不打断动作）",
                    player.IsPlaying && window.ShowingAnimationFrame,
                    $"播放中={player.IsPlaying} 贴的是动画帧={window.ShowingAnimationFrame}");

                player.AdvanceBy(TimeSpan.FromSeconds(2));   // 10fps × 2 帧，足够播完

                check("悬浮期间动画播完，落回的是悬浮形象（不是本体）",
                    !player.IsPlaying && window.ShowingHoverImage,
                    $"播放中={player.IsPlaying} 贴悬浮图={window.ShowingHoverImage}");
            }
            else
            {
                check("悬浮态下能播动画（探针素材应当可用）", false, "Play 返回 false");
            }

            // 移出走的是真实的 OnMouseLeave：里面"要不要自动收起"那个分支会提前 return，
            // 顺序写反就会停在被换掉的那张上 —— 这正是这条断言要守的东西
            window.SimulateMouseLeave();
            window.UpdateLayout();

            check("鼠标移出换回本体（自动收起关着时也必须换回来）",
                window.ShowingStillImage,
                $"贴悬浮图={window.ShowingHoverImage}，贴本体={window.ShowingStillImage}");

            // 连进两次不该把形象弄丢（真实环境里移入会重复触发）
            window.SimulateMouseEnter();
            window.SimulateMouseEnter();

            check("重复移入不会把形象弄丢", window.ShowingHoverImage, "仍应贴悬浮那张");
        }
        catch (Exception ex)
        {
            check("悬浮态形象", false, ex.Message);
            sb.AppendLine("      " + ex);
        }
        finally
        {
            // 顺序和动画那段一致：先关窗口（它还挂着播放器），再还原环境变量和缓存
            try { window?.Close(); }
            catch { /* 关不掉不影响结论 */ }

            try
            {
                Environment.SetEnvironmentVariable("WHALELOG_ASSET_DIR", previousAssetDir);
                IconAssets.Reload();
            }
            catch { /* 还原失败也只能继续 */ }

            try { Directory.Delete(probeRoot, true); }
            catch { /* 删不掉不影响结论 */ }
        }
    }

    /// <summary>
    /// 在临时素材目录里造一份最小可用的动画素材（真 PNG + 真帧表 + 同画布静态本体）。
    /// 「小鲸鱼动画」和「悬浮态形象」两段探针共用它。
    /// </summary>
    /// <summary>
    /// 形象包：新版溟月与原版鲸鱼娘并存、能一键切换。
    ///
    /// 这是"加了个开关、但开关其实没接上"的高发区，而且失效方式很安静：
    /// 切换没生效只是"看起来还是原来那只鲸鱼"，没人会想到去查配置。
    /// 这里自己造两套形象（本体用蓝/绿两种纯色区分），把切换真的走一遍。
    ///
    /// 另外钉死一条兜底规则：**任何情况下都要解析得到一个能用的形象**。
    /// 用户把素材目录删了、或者从旧版本升级上来（配置里没这个字段），
    /// 界面都不能空着 —— 那看起来就像程序坏了。
    /// </summary>
    private static void ProbeCharacterPacks(CheckFn check, StringBuilder sb)
    {
        // 先照一遍**真实素材目录**（不是下面那个探针目录），并且只用 info 行、不做断言。
        //
        // 这一段的价值全在排障：设置里「形象」那一排按钮是按"程序旁边实际有哪些形象包"
        // 动态生成的，某套素材不在，那个按钮就是灰的、点上去毫无反应。
        // 用户报"点新形象没反应"时，日志里有这几行就能一眼定位，不用猜。
        // 不写成断言是因为"故意删掉素材瘦身"是允许的，那不算故障。
        var live = CharacterPacks.Available();
        sb.AppendLine($"  素材目录：{AppPaths.AssetsDir}");
        foreach (var pack in live)
        {
            sb.AppendLine(pack.IsAvailable
                ? $"    [有] {pack.Title}（{pack.Id}）"
                : $"    [缺] {pack.Title}（{pack.Id}）→ 期望 {pack.Root}");
        }
        if (!CharacterPacks.IsAvailable(CharacterPacks.DshId))
        {
            sb.AppendLine("    ↑ 缺的是随包发的那个形象：设置里的按钮会显示成灰的「（缺素材）」，" +
                          "点它不会有任何反应。把形象包放进上面的目录再重启即可。");
        }

        var probeRoot = Path.Combine(AppPaths.LogDir, "pack-probe");
        var assetDir = Path.Combine(probeRoot, "Assets");

        string? previousAssetDir = null;
        FloatingWindow? window = null;
        var settings = App.Settings.Settings;
        var previousCharacter = settings.FloatingCharacter;
        var previousAnimations = settings.FloatingAnimations;

        try
        {
            previousAssetDir = Environment.GetEnvironmentVariable("WHALELOG_ASSET_DIR");

            settings.FloatingAutoCollapse = false;
            settings.FloatingLeft = Offscreen;
            settings.FloatingTop = Offscreen;
            settings.FloatingAnimations = true;
            settings.FloatingCharacter = CharacterPacks.ClassicId;   // 先把"从原版换到新版"这条路走出来

            // 原版：素材根下直接放（老布局）；新版：characters\dsh\ 下。
            // 本体用不同尺寸 + 不同颜色，切换有没有生效一眼可辨。
            Directory.CreateDirectory(assetDir);
            WriteProbeSolid(Path.Combine(assetDir, "whale.png"), 200, 200, 0, 0, 255);

            var packDir = CharacterPacks.DirOf(CharacterPacks.DshId, assetDir);
            Directory.CreateDirectory(packDir);
            WriteProbeSolid(Path.Combine(packDir, "whale.png"), 384, 384, 0, 200, 0);

            // ---- 纯逻辑部分（不起窗口）----
            check("设置里没选过时默认用新形象（装了新素材就该看到新的）",
                CharacterPacks.Resolve("", assetDir) == CharacterPacks.DshId,
                $"解析结果：{CharacterPacks.Resolve("", assetDir)}");

            // 「设置里的值给不出一个能用的形象」有三级降级，这里量前两级：
            //   设置里那个（可用就用）→ 默认形象 dsh → 原版。
            // 不存在的 ID（手改过 settings.json、自建的形象包被删了）落到的**是默认形象**，
            // 不是原版 —— 和"没选过"同一个落点，因为两者对用户是同一件事："这个值不作数"。
            // 原版只在默认形象也不在时才兜底（下一条）。
            check("设置里是个不存在的形象时退回默认形象，而不是解析出空",
                CharacterPacks.Resolve("根本没有这个形象", assetDir) == CharacterPacks.DshId,
                $"解析结果：{CharacterPacks.Resolve("根本没有这个形象", assetDir)}");

            // 默认形象也拿不到时（素材没随包发、用户删了 characters\），必须落到原版 ——
            // 原版的图是编译进程序集的嵌入资源，素材目录整个不存在也在。
            check("默认形象也不在时不存在的 ID 落到原版（原版是嵌入资源，永远能用）",
                CharacterPacks.Resolve("根本没有这个形象", Path.Combine(probeRoot, "empty-assets"))
                    == CharacterPacks.ClassicId,
                $"解析结果：{CharacterPacks.Resolve("根本没有这个形象", Path.Combine(probeRoot, "empty-assets"))}");

            check("形象 ID 里被塞了路径分隔符也不会越出素材目录",
                CharacterPacks.DirOf("..\\..\\Windows", assetDir) == assetDir,
                CharacterPacks.DirOf("..\\..\\Windows", assetDir));

            // ---- 真的换一遍 ----
            Environment.SetEnvironmentVariable("WHALELOG_ASSET_DIR", assetDir);

            window = new FloatingWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                BackgroundProbeMode = true,
                Left = Offscreen,
                Top = Offscreen,
            };
            window.Show();
            window.UpdateLayout();

            check("按设置里选的原版形象起窗口",
                window.CharacterId == CharacterPacks.ClassicId,
                $"实际形象：{window.CharacterId}");

            check("原版用的是素材根下那张 200×200 的本体（老布局没被打乱）",
                window.StillImageSize == (200, 200),
                $"实际 {FormatSize(window.StillImageSize)}");

            // 换成新形象：只需改设置 + 重新套用，不该要求重启
            settings.FloatingCharacter = CharacterPacks.DshId;
            window.ApplySettings();
            window.UpdateLayout();

            check("切换到新形象立刻生效（不用重启）",
                window.CharacterId == CharacterPacks.DshId,
                $"实际形象：{window.CharacterId}（{window.CharacterTitle}）");

            check("换形象之后本体也换成了新版那张 384×384",
                window.StillImageSize == (384, 384),
                $"实际 {FormatSize(window.StillImageSize)}");

            check("新形象的素材根是它自己的目录（不是素材根）",
                window.CharacterRoot.EndsWith(Path.Combine(CharacterPacks.FolderName, CharacterPacks.DshId),
                                             StringComparison.Ordinal),
                window.CharacterRoot);

            // 新版没有闭眼差分，就**不该**拿内置那张原版闭眼图去顶替 ——
            // 那看起来是"换了形象、鼠标一移上去变回旧鲸鱼"
            check("新形象没有差分图时不会回落成原版的闭眼图",
                !window.HasHoverImage || window.CharacterIsClassic,
                $"配上的差分图：{(window.HasHoverImage ? "有" : "没有")}");

            // 切回原版：开关是双向的
            settings.FloatingCharacter = CharacterPacks.ClassicId;
            window.ApplySettings();
            window.UpdateLayout();

            check("能切回原版形象（开关是双向的）",
                window.CharacterId == CharacterPacks.ClassicId && window.StillImageSize == (200, 200),
                $"形象={window.CharacterId} 本体={FormatSize(window.StillImageSize)}");

            // 素材整个不存在时也不能空着：删掉 characters\ 之后仍然要有一个能用的形象
            settings.FloatingCharacter = CharacterPacks.DshId;
            try { Directory.Delete(packDir, true); } catch { /* 删不掉就用下面的断言兜 */ }

            window.ApplySettings();
            window.UpdateLayout();

            check("形象素材被删掉之后自动退回原版（界面不会空，也不会起不来）",
                window.CharacterId == CharacterPacks.ClassicId,
                $"实际形象：{window.CharacterId}");

            check("界面能列出可选形象（内置两个排在最前）",
                CharacterPacks.Available(assetDir).Count >= 2,
                string.Join("、", CharacterPacks.Available(assetDir).Select(p => p.Id)));
        }
        catch (Exception ex)
        {
            check("形象包切换", false, ex.Message);
            sb.AppendLine("      " + ex);
        }
        finally
        {
            try { window?.Close(); } catch { /* 关不掉不影响结论 */ }

            settings.FloatingCharacter = previousCharacter;
            settings.FloatingAnimations = previousAnimations;

            try
            {
                Environment.SetEnvironmentVariable("WHALELOG_ASSET_DIR", previousAssetDir);
                IconAssets.Reload();
            }
            catch { /* 还原失败也只能继续 */ }

            try { Directory.Delete(probeRoot, true); } catch { /* 删不掉不影响结论 */ }
        }
    }

    /// <summary>
    /// 设置页那一排「形象」按钮。
    ///
    /// 为什么要单独真起一个窗口量它：形象切换是纯界面的事，上一节
    /// <see cref="ProbeCharacterPacks"/> 只证明"解析函数返回对了"。而用户 2026-09-20
    /// 报的「设置里点新形象没有反应」，代码一行都没错 —— 是按钮被禁用了
    /// （那个构建旁边的 <c>Assets\characters\</c> 根本不存在）。禁用按钮点下去不抛异常、
    /// 不报错、日志里一个字都没有，只有真去问"它能不能点"才测得到。
    ///
    /// 两支都要走：素材在 → 按钮必须可点、点了设置真跟着变；素材不在 → 按钮必须禁用，
    /// 而且**原因要写在说明行里**（只写在 ToolTip 里等于没写，没人会去悬停一个灰按钮）。
    /// </summary>
    private static void ProbeCharacterChipsPage(CheckFn check, StringBuilder sb)
    {
        var probeRoot = Path.Combine(AppPaths.LogDir, "chip-probe");
        var emptyAssets = Path.Combine(probeRoot, "empty-assets");

        MainWindow? window = null;
        string? previousAssetDir = null;
        var settings = App.Settings.Settings;
        var previousCharacter = settings.FloatingCharacter;

        try
        {
            previousAssetDir = Environment.GetEnvironmentVariable("WHALELOG_ASSET_DIR");
            Directory.CreateDirectory(emptyAssets);

            window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Left = Offscreen,
                Top = Offscreen,
            };
            window.Show();
            window.SwitchTo(NavPage.Settings);
            window.UpdateLayout();

            var packs = CharacterPacks.Available();

            check("设置页列出的形象按钮数 = 扫到的形象包数",
                window.CharacterChipCount == packs.Count,
                $"按钮 {window.CharacterChipCount} 个；形象包 {packs.Count} 套：" +
                string.Join("、", packs.Select(p => p.Id)));

            // 「点了没反应」的直接原因就是这一条。
            var dead = packs.Where(p => p.IsAvailable && !window.CharacterChipState(p.Id).Enabled)
                            .Select(p => p.Id).ToList();

            check("素材在的形象，按钮就是能点的（灰按钮 = 点了没反应）",
                dead.Count == 0,
                dead.Count == 0 ? "全部可点" : "被禁用：" + string.Join("、", dead));

            var dsh = window.CharacterChipState(CharacterPacks.DshId);
            sb.AppendLine($"  溟月那个按钮：{(dsh.Found ? dsh.Text : "没找到")}｜" +
                          $"可点={(dsh.Enabled ? "是" : "否")}｜选中={(dsh.Checked ? "是" : "否")}");

            if (dsh.Enabled)
            {
                // 点**没被选中**的那一个。RadioButton 把 IsChecked 从 true 置成 true
                // 不会重抛 Checked —— 点一个已经选中的按钮本来就是无操作，不是 bug。
                // 这里要验的是"这一下点下去有没有用"，所以必须点在另一个上。
                var clickedClassic = window.ClickCharacterChip(CharacterPacks.ClassicId);

                check("在设置页点「原版」，设置里真的跟着改了",
                    clickedClassic && settings.FloatingCharacter == CharacterPacks.ClassicId,
                    $"点得动={clickedClassic}；设置里现在是「{settings.FloatingCharacter}」");

                window.ReloadCharacterChips();
                var classicAfterReload = window.CharacterChipState(CharacterPacks.ClassicId);

                check("重建按钮后高亮的跟着设置走（不会停在旧的那个上）",
                    classicAfterReload.Checked
                        && !window.CharacterChipState(CharacterPacks.DshId).Checked,
                    $"原版选中={classicAfterReload.Checked}，" +
                    $"溟月选中={window.CharacterChipState(CharacterPacks.DshId).Checked}");

                var clickedDsh = window.ClickCharacterChip(CharacterPacks.DshId);

                check("再点回「溟月」也点得动（开关是双向的）",
                    clickedDsh && settings.FloatingCharacter == CharacterPacks.DshId,
                    $"点得动={clickedDsh}；设置里现在是「{settings.FloatingCharacter}」");
            }
            else
            {
                sb.AppendLine($"  按钮是灰的（素材不在 {CharacterPacks.DirOf(CharacterPacks.DshId)}），" +
                              "跳过点击 —— 这正是用户报的「点了没反应」");
            }

            // ---- 素材不在的那一支 ----
            Environment.SetEnvironmentVariable("WHALELOG_ASSET_DIR", emptyAssets);
            window.ReloadCharacterChips();
            window.UpdateLayout();

            var missing = window.CharacterChipState(CharacterPacks.DshId);
            var hint = window.CharacterHintText;

            check("素材不在时，那套形象的按钮禁用并标出「缺素材」",
                missing.Found && !missing.Enabled && missing.Text.Contains("缺素材"),
                $"在={missing.Found}｜可点={missing.Enabled}｜按钮文字：{missing.Text}");

            check("禁用的原因写在说明行里（不是只藏在悬停提示）",
                hint.Contains("缺素材") &&
                hint.Contains(CharacterPacks.DirOf(CharacterPacks.DshId, emptyAssets)),
                hint.Replace(Environment.NewLine, " ／ "));

            check("素材不在时点它无效，也不会把设置改坏",
                !window.ClickCharacterChip(CharacterPacks.DshId),
                $"点击返回={(window.ClickCharacterChip(CharacterPacks.DshId) ? "true" : "false")}");
        }
        catch (Exception ex)
        {
            check("设置页的形象按钮", false, ex.Message);
            sb.AppendLine("      " + ex);
        }
        finally
        {
            try { window?.Close(); } catch { /* 关不掉不影响结论 */ }

            // 这里走的是真按钮 → 真的落了盘，所以必须把原值写回去，别动用户的设置。
            try { App.Settings.Update(s => s.FloatingCharacter = previousCharacter); }
            catch { /* 写不回去也只能继续 */ }

            try
            {
                Environment.SetEnvironmentVariable("WHALELOG_ASSET_DIR", previousAssetDir);
                IconAssets.Reload();
            }
            catch { /* 还原失败也只能继续 */ }

            try { Directory.Delete(probeRoot, true); } catch { /* 删不掉不影响结论 */ }
        }
    }

    /// <summary>
    /// 透明视频的**播放链路**实测：起 ffmpeg → 解码 → 贴到鲸鱼上 → 播完自己收手。
    ///
    /// 这条链路跨越进程边界（ffmpeg 是外部程序），任何一环断掉的表现都是
    /// "点下去什么都不发生"，而日志里一个字都没有 —— 所以必须真跑一遍。
    /// 探针素材用普通视频（不带 alpha）就够：解码、裁剪缩放、贴帧、收尾
    /// 这几步和真的透明 webm 走的是同一套代码。
    /// </summary>
    private static void ProbeVideoPlayback(CheckFn check, StringBuilder sb)
    {
        var ffmpeg = FfmpegLocator.Path;
        sb.AppendLine(Indent(FfmpegLocator.Describe()));

        if (ffmpeg == null)
        {
            sb.AppendLine("      （本机没找到 ffmpeg，跳过透明视频的播放实测）");
            return;
        }

        var probeRoot = Path.Combine(AppPaths.LogDir, "video-probe");
        var assetDir = Path.Combine(probeRoot, "Assets");
        var clip = Path.Combine(probeRoot, "clip.avi");

        string? previousAssetDir = null;
        FloatingWindow? window = null;
        var settings = App.Settings.Settings;
        var previousCharacter = settings.FloatingCharacter;
        var previousAnimations = settings.FloatingAnimations;

        const int cell = 384;
        var probeActions = new[] { "clickBounce-探针点击跃动", "ambient-探针闲置小动作" };

        try
        {
            previousAssetDir = Environment.GetEnvironmentVariable("WHALELOG_ASSET_DIR");

            settings.FloatingAutoCollapse = false;
            settings.FloatingLeft = Offscreen;
            settings.FloatingTop = Offscreen;
            settings.FloatingAnimations = true;
            settings.FloatingCharacter = CharacterPacks.ClassicId;

            Directory.CreateDirectory(probeRoot);
            Directory.CreateDirectory(assetDir);

            if (!TryMakeProbeClip(ffmpeg, clip, frames: 24, side: 192, out var makeError))
            {
                check("造一段测试视频", false, makeError);
                return;
            }

            // 源素材只有 192×192，靠帧表里的滤镜放成 384 的格子 ——
            // 顺便验证「运行时真的按帧表的滤镜解码」（滤镜写错/没生效时格子尺寸会不对）
            foreach (var action in probeActions)
                WriteProbeVideoAction(assetDir, action, clip, cell);

            WriteProbeSolid(Path.Combine(assetDir, "whale.png"), cell, cell, 0, 0, 255);

            Environment.SetEnvironmentVariable("WHALELOG_ASSET_DIR", assetDir);

            window = new FloatingWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                BackgroundProbeMode = true,
                Left = Offscreen,
                Top = Offscreen,
            };
            window.Show();
            window.UpdateLayout();

            check("视频动作都登记进了播放器",
                window.RegisteredActionCount == probeActions.Length && window.VideoActionCount == probeActions.Length,
                $"登记 {window.RegisteredActionCount} 个、其中视频 {window.VideoActionCount} 个");

            check("视频动作按目录名认出了角色（点击 / 闲置小动作）",
                window.ClickActionCount == 1 && window.AmbientActionCount == 1,
                $"点击 {window.ClickActionCount}、闲置小动作 {window.AmbientActionCount}");

            check("鲸鱼框按视频帧表算出了正确高度",
                Math.Abs(window.WhaleHeight - 198) < 1.5,
                $"实际 {window.WhaleHeight:0.#}px（静态形象是 168px）");

            // ---- 真的播一段 ----
            check("能起播视频动作", window.PlayRoleOnce(AnimActions.Click),
                $"当前动作：{window.CurrentAnimation ?? "（没有）"}");

            check("播的是视频那条路（不是精灵图）", window.IsVideoPlaying,
                $"当前动作：{window.CurrentAnimation ?? "（没有）"}");

            var frames = window.PumpVideoFrames(4, 8000);

            check("视频帧真的被解出来并贴到鲸鱼上了", frames >= 4,
                $"解出 {frames} 帧（起不来 / 解不出帧时这里会是 0）");

            check("鲸鱼上贴的确实是一帧动画，而不是静止底图",
                window.ShowingAnimationFrame && window.AnimPlayer?.FrameShown == true,
                $"贴的是 {DescribeSource(window)}");

            var whale = Descendants(window).OfType<Image>().FirstOrDefault(i => i.Name == "WhaleImage");
            var frameBitmap = whale?.Source as System.Windows.Media.Imaging.WriteableBitmap;

            check("解出来的帧尺寸和帧表的格子一致（滤镜真按帧表生效了）",
                frameBitmap != null && frameBitmap.PixelWidth == cell && frameBitmap.PixelHeight == cell,
                frameBitmap == null ? "没拿到帧位图" : $"{frameBitmap.PixelWidth}×{frameBitmap.PixelHeight}，期望 {cell}×{cell}");

            // ---- 非循环动作播完要自己收手 ----
            var waited = window.PumpUntilStopped(20000);

            check("非循环视频播完自己收手（不会永远停在「播放中」堵住后面的动作）",
                !window.IsAnimationPlaying,
                $"等了 {waited} ms，当前动作={window.CurrentAnimation ?? "（没有，已停）"}");

            check("播完把静止的底图贴回来了（不留最后一帧在脸上）",
                whale != null && !window.ShowingAnimationFrame,
                $"贴的是 {DescribeSource(window)}");
        }
        catch (Exception ex)
        {
            check("透明视频播放", false, ex.Message);
            sb.AppendLine("      " + ex);
        }
        finally
        {
            try { window?.Close(); } catch { /* 关不掉不影响结论 */ }

            settings.FloatingCharacter = previousCharacter;
            settings.FloatingAnimations = previousAnimations;

            try
            {
                Environment.SetEnvironmentVariable("WHALELOG_ASSET_DIR", previousAssetDir);
                IconAssets.Reload();
            }
            catch { /* 还原失败也只能继续 */ }

            try { Directory.Delete(probeRoot, true); } catch { /* 删不掉不影响结论 */ }
        }
    }

    /// <summary>自检用：把一个像素尺寸写成「宽×高」，取不到时写「（没取到）」。公共用。</summary>
    private static string FormatSize((int Width, int Height)? size)
        => size.HasValue ? $"{size.Value.Width}×{size.Value.Height}" : "（没取到）";

    /// <summary>自检用：此刻鲸鱼上贴的是什么（写断言失败信息时很有用）。</summary>
    private static string DescribeSource(FloatingWindow window)
    {
        if (window.AnimPlayer?.CurrentIsVideo == true && window.AnimPlayer.FrameShown)
            return "视频解出来的一帧";

        if (window.ShowingAnimationFrame) return "精灵图切出来的一帧";
        if (window.ShowingHoverImage) return "悬浮差分图";
        if (window.ShowingStillImage) return "静态本体";
        return "（认不出来）";
    }

    /// <summary>
    /// 透明 webm 的 alpha 通道到底解出来没有。
    ///
    /// 这一节是补的账：1.4.1 之前形象动画一直顶着黑底显示 —— ffmpeg 默认挑的
    /// native vp9 解码器会把 alpha（VP9 把它存在「附加数据」里）整条丢掉，
    /// 解出来照样是一帧帧 RGBA，只是 alpha 位全填 255。
    /// 当时的自检只断言了"帧数对不对""每帧字节数对不对"，这两条它**全过** ——
    /// 于是黑底顺利混进了出厂包，直到有人肉眼看见。
    ///
    /// 所以这里不看帧数，**直接数像素里的透明值**：现造一段左半边不透明、
    /// 右半边全透明的 VP9 素材，解一帧出来数 alpha。全 255 就说明透明丢了。
    /// </summary>
    private static void ProbeVideoAlpha(CheckFn check, StringBuilder sb)
    {
        var ffmpeg = FfmpegLocator.Path;
        if (ffmpeg == null)
        {
            sb.AppendLine(Indent("（本机没找到 ffmpeg，跳过透明通道检查）"));
            return;
        }

        const int side = 64;
        var probeRoot = Path.Combine(AppPaths.LogDir, "video-alpha-probe");
        var clip = Path.Combine(probeRoot, "alpha.webm");

        try
        {
            Directory.CreateDirectory(probeRoot);

            if (!TryMakeProbeAlphaClip(ffmpeg, clip, side, out var makeError))
            {
                check("造一段带透明通道的测试素材", false, makeError);
                return;
            }

            check("测试素材确实是 VP9（透明得靠 libvpx 才解得出来）",
                VideoFrameStream.IsVp9Webm(clip), Path.GetFileName(clip));

            check("这台 ffmpeg 带 libvpx-vp9 解码器",
                VideoFrameStream.FfmpegSupportsVp9Alpha(ffmpeg),
                "缺这个解码器时透明会丢，角色会带黑底");

            using var stream = new VideoFrameStream(ffmpeg, clip, filter: "", cellSide: side, loop: false);

            check("透明素材点名了 libvpx 解码器", stream.UsingLibvpx,
                stream.UsingLibvpx ? "已点名" : "退回了 native —— 透明会丢");

            check("素材被判定为「带透明」", stream.HasAlpha);

            if (!stream.TryTake(out var frame, 10000) || frame == null)
            {
                check("解出一帧来数像素", false, "等了 10 秒没取到帧：" + stream.Error);
                return;
            }

            int clear = 0, opaque = 0;
            for (int i = 3; i < frame.Length; i += 4)
            {
                if (frame[i] == 0) clear++;
                else if (frame[i] == 255) opaque++;
            }

            int total = frame.Length / 4;

            // 这是整节的重点：native vp9 解出来的帧在这一条上会是 0 个透明像素
            check("解出来的帧真有透明像素（alpha 没被丢掉）",
                clear > 0,
                $"全透明 {clear}/{total} 个像素" +
                (clear == 0 ? " —— alpha 整条丢了，角色会带着黑底显示" : ""));

            check("解出来的帧也有实心像素（没把角色一起弄透明）",
                opaque > 0, $"全不透明 {opaque}/{total} 个像素");
        }
        catch (Exception ex)
        {
            check("透明通道", false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(probeRoot, true); } catch { /* 删不掉不影响结论 */ }
        }
    }

    /// <summary>
    /// 造一段**带真 alpha** 的 VP9 素材：左半边不透明的红、右半边全透明。
    ///
    /// 用 geq 直接算 alpha（X 是像素横坐标），于是透明是"结构性"的 ——
    /// 左半必须是 255、右半必须是 0，解码对不对一眼能判，不用赌编码器的取舍。
    /// </summary>
    private static bool TryMakeProbeAlphaClip(string ffmpeg, string path, int side, out string error)
    {
        error = "";

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            void Arg(string a) => psi.ArgumentList.Add(a);

            // a='...' 上的单引号是 ffmpeg 滤镜语法里的转义，不是 shell 的：
            // if(lt(X,32),255,0) 里的逗号不引起来会被当成「下一个滤镜」
            Arg("-v"); Arg("error");
            Arg("-f"); Arg("lavfi");
            Arg("-i"); Arg($"nullsrc=s={side}x{side}:r=24,format=rgba," +
                           $"geq=r='255':g='0':b='0':a='if(lt(X,{side / 2}),255,0)'");
            Arg("-frames:v"); Arg("24");
            Arg("-c:v"); Arg("libvpx-vp9");
            Arg("-pix_fmt"); Arg("yuva420p");
            Arg("-y"); Arg(path);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) { error = "进程没起来"; return false; }

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(20000);

            if (File.Exists(path) && new FileInfo(path).Length > 0) return true;

            error = stderr.Length > 300 ? stderr[^300..] : stderr;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>造一个视频动作目录（帧表里用滤镜把源素材放成格子大小）。</summary>
    private static void WriteProbeVideoAction(string assetDir, string action, string clipFile, int cell)
    {
        var dir = Path.Combine(assetDir, AnimLibrary.FolderName, action);
        Directory.CreateDirectory(dir);

        File.Copy(clipFile, Path.Combine(dir, "video.webm"), overwrite: true);

        File.WriteAllText(Path.Combine(dir, "anim.json"), $$"""
        {
          "name": "{{action}}", "version": 1, "type": "video",
          "video": "video.webm",
          "filter": "scale={{cell}}:{{cell}}",
          "frameWidth": {{cell}}, "frameHeight": {{cell}},
          "cols": 1, "rows": 1, "count": 24, "fps": 24,
          "loop": false,
          "anchor": { "x": {{cell / 2}}, "y": {{cell}} },
          "characterHeight": {{cell * 600 / 720}},
          "returnTo": "base", "sound": ""
        }
        """);
    }

    /// <summary>用 ffmpeg 现造一段小视频（不往仓库里塞二进制素材）。</summary>
    private static bool TryMakeProbeClip(string ffmpeg, string path, int frames, int side, out string error)
    {
        error = "";

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            void Arg(string a) => psi.ArgumentList.Add(a);

            Arg("-v"); Arg("error");
            Arg("-f"); Arg("lavfi");
            Arg("-i"); Arg($"testsrc=size={side}x{side}:rate=24");
            Arg("-frames:v"); Arg(frames.ToString());
            Arg("-c:v"); Arg("mpeg4");
            Arg("-y"); Arg(path);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) { error = "进程没起来"; return false; }

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(20000);

            if (File.Exists(path) && new FileInfo(path).Length > 0) return true;

            error = stderr.Length > 300 ? stderr[^300..] : stderr;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void WriteProbeAnimation(
        string assetDir, string action, int frameSize, int cols, int count)
    {
        var actionDir = Path.Combine(assetDir, AnimLibrary.FolderName, action);
        Directory.CreateDirectory(actionDir);

        WriteProbeSheet(Path.Combine(actionDir, "sheet.png"), frameSize, cols, count);

        File.WriteAllText(Path.Combine(actionDir, AnimLibrary.ManifestName), $$"""
        {
          "name": "{{action}}", "version": 1, "type": "sheet",
          "sheet": "sheet.png",
          "frameWidth": {{frameSize}}, "frameHeight": {{frameSize}},
          "cols": {{cols}}, "rows": 1, "count": {{count}},
          "fps": 10, "loop": false,
          "anchor": { "x": 360, "y": 660 },
          "characterHeight": 600, "returnTo": "base", "sound": ""
        }
        """);

        // 同画布的静态本体，让"静止 → 播放"不跳
        WriteProbeSheet(Path.Combine(assetDir, AnimLibrary.FolderName, AnimLibrary.BaseImageName),
            frameSize, 1, 1);
    }

    /// <summary>造一张纯色 PNG。用来区分"此刻贴的是本体还是悬浮态那张图"。</summary>
    private static void WriteProbeSolid(string path, int width, int height, byte r, byte g, byte b)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int p = y * stride + x * 4;
                pixels[p] = b;
                pixels[p + 1] = g;
                pixels[p + 2] = r;
                pixels[p + 3] = 255;
            }
        }

        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>
    /// 造一张只有两块色板的测试精灵图。
    ///
    /// 必须是**真 PNG**：播放器走的是真实的图片解码路径，用假文件测不出解码、
    /// 尺寸核对、切片这些环节。两帧颜色不同，出问题时一眼能看出是没切帧还是切错了。
    /// </summary>
    private static void WriteProbeSheet(string path, int frameSize, int cols, int count)
    {
        int w = frameSize * cols;
        int h = frameSize;
        int stride = w * 4;
        var pixels = new byte[stride * h];

        for (int i = 0; i < count; i++)
        {
            // 0 = 蓝、1 = 红（BGRA 顺序）
            byte blue = (byte)(i == 0 ? 255 : 0);
            byte red = (byte)(i == 0 ? 0 : 255);

            for (int y = frameSize / 4; y < frameSize * 3 / 4; y++)
            {
                for (int x = i * frameSize + frameSize / 4; x < i * frameSize + frameSize * 3 / 4; x++)
                {
                    int p = y * stride + x * 4;
                    pixels[p] = blue;
                    pixels[p + 1] = 0;
                    pixels[p + 2] = red;
                    pixels[p + 3] = 255;
                }
            }
        }

        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>多行文本在报告里统一往右缩进，读起来才不像掉进了另一份文件。</summary>
    private static string Indent(string text)
        => "  " + text.Replace(Environment.NewLine, Environment.NewLine + "  ");

    /// <summary>悬浮窗展开态的设计宽高，和 FloatingWindow 里的常量保持一致。</summary>
    private const double ExpandedW = 236;
    private const double ExpandedH = 296;

    /// <summary>可视树里的所有后代。用来确认界面真的渲染出了分组栏头。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root == null) yield break;

        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grand in Descendants(child)) yield return grand;
        }
    }

    /// <summary>
    /// 一个 GroupItem 栏头里的文字。
    ///
    /// GroupItem 的默认模板是「上标题下条目」的两段式，所以第一个孩子是栏头、
    /// 第二个孩子才是文件条目 —— 只取栏头那一支，免得把卡片里的文字也算进来。
    /// </summary>
    private static IReadOnlyList<TextBlock> HeaderTexts(GroupItem group)
    {
        if (System.Windows.Media.VisualTreeHelper.GetChildrenCount(group) == 0)
            return Array.Empty<TextBlock>();

        var templateRoot = System.Windows.Media.VisualTreeHelper.GetChild(group, 0);
        if (System.Windows.Media.VisualTreeHelper.GetChildrenCount(templateRoot) == 0)
            return Array.Empty<TextBlock>();

        var header = System.Windows.Media.VisualTreeHelper.GetChild(templateRoot, 0);
        return Descendants(header).OfType<TextBlock>().ToList();
    }

    /// <summary>屏幕外的坐标，用来摆只在后台量数字的探针窗口。</summary>
    private const double Offscreen = -32000;

    /// <summary>
    /// 今日记录页里那段「归档的文件」。
    ///
    /// 需求本身就很适合断言，两句话对应两个硬性检查：
    ///   "样式和文件归档内保持一致" → 两处列表必须是同一个模板对象
    ///   "多条时堆叠不要重叠"       → 相邻卡片的矩形不能相交，还得留下间距
    /// </summary>
    private static void ProbeDayFileList(CheckFn check)
    {
        MainWindow? window = null;
        try
        {
            window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Left = -32000,
                Top = -32000,
            };
            window.Show();
            window.UpdateLayout();          // 今日记录是默认页，不必 SwitchTo

            check("今日记录页和文件归档页共用同一个卡片模板",
                window.ArchiveFileTemplate != null &&
                ReferenceEquals(window.ArchiveFileTemplate, window.NotesFileTemplate),
                $"归档页 {window.ArchiveFileTemplate?.GetType().Name ?? "(空)"} / " +
                $"记录页 {window.NotesFileTemplate?.GetType().Name ?? "(空)"}");

            int expected = App.Store.FilesOnDate(DateTime.Today).Count();

            check("卡片条数和当天的归档条数一致",
                window.DayFileCardCount == expected,
                $"界面 {window.DayFileCardCount} 条，存储 {expected} 条");

            check("当天没有文件时整段收起来",
                (expected > 0) == window.DayFilesSectionVisible,
                $"当天 {expected} 个文件，段落{(window.DayFilesSectionVisible ? "显示" : "隐藏")}");

            var bounds = window.DayFileCardBounds;

            if (expected == 0)
            {
                check("没有文件时不产生隐藏的卡片", bounds.Count == 0, $"{bounds.Count} 张");
                return;
            }

            check("每张卡片都完成了布局",
                bounds.Count == expected && bounds.All(b => b.Width > 50 && b.Height > 20),
                $"{bounds.Count} 张：" + string.Join("、", bounds.Select(b => $"{b.Width:0}×{b.Height:0}")));

            // 只有一张卡片时没有"相邻"可言。下面的 minGap 会停在初始的
            // double.MaxValue，不显式跳过就会报出一个天文数字的假失败 ——
            // 而用户真实数据里经常只有一两条文件。
            if (bounds.Count < 2)
            {
                check("卡片不足两张，无需检查堆叠与间距", true, $"当天 {bounds.Count} 张卡片");
                return;
            }

            // 堆叠：后一张必须整张落在前一张下面，既不能压住也不能贴着
            string? problem = null;
            double minGap = double.MaxValue;

            for (int i = 1; i < bounds.Count; i++)
            {
                double gap = bounds[i].Top - bounds[i - 1].Bottom;
                minGap = Math.Min(minGap, gap);

                if (gap < -0.5)
                {
                    problem = $"第 {i} 张和第 {i + 1} 张重叠了 {-gap:0.#}px";
                    break;
                }
            }

            check("多条文件依次向下堆叠、互不重叠",
                problem == null,
                problem ?? $"{bounds.Count} 张卡片，相邻最小间隙 {minGap:0.#}px");

            check("卡片之间留了下外边距（不会挤在一起）",
                bounds.Count > 1 && minGap >= 0 && bounds.All(b => b.Height > 30),
                $"最小间隙 {minGap:0.#}px，卡片高 {bounds.FirstOrDefault().Height:0.#}px");

            // 这一段的标题必须跟着当前查看的日期走
            var title = window.FindName("DayFilesTitle") as TextBlock;
            check("标题写明是哪一天归档的",
                title != null && title.Text.Contains("归档的文件", StringComparison.Ordinal),
                title?.Text ?? "（找不到标题）");
        }
        catch (Exception ex)
        {
            check("今日记录里的归档文件", false, ex.Message);
        }
        finally
        {
            try { window?.Close(); }
            catch { /* 关不掉不影响结论 */ }
        }
    }

    /// <summary>
    /// 「全部」视图按归档日期分组。
    ///
    /// 期望值刻意在断言里独立算一遍（而不是调用被测的 DateGrouping），
    /// 这样"排序函数本身写错"也能被抓住，不会自证。
    /// </summary>
    private static void ProbeFileDateGrouping(CheckFn check)
    {
        MainWindow? window = null;
        try
        {
            window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Left = -32000,
                Top = -32000,
            };
            window.Show();
            window.SwitchTo(NavPage.Files);   // 页面 Collapsed 时不会生成条目容器
            window.UpdateLayout();

            // 只看当天：所有记录本来就是同一天，再加栏头是噪音
            check("只看当天不加日期栏头", window.FileGroupCount == 0, window.FileGroupReport);

            window.SetFileFilter(true);
            window.UpdateLayout();

            var all = App.Store.QueryFiles(null).ToList();

            // 独立算一遍期望：日期键去重后倒序，没有日期的排最后
            static string GroupKey(FileRecord f)
                => string.IsNullOrEmpty(f.AddedDate) ? "" : f.AddedDate;

            var expectOrder = all
                .Select(GroupKey)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(k => k, StringComparer.Ordinal)
                .ToList();

            var groups = window.FileGroups;

            if (all.Count == 0)
            {
                check("没有文件时不产生空栏头", groups.Count == 0, window.FileGroupReport);
            }
            else
            {
                check("全部视图按归档日期分组",
                    groups.Select(g => g.Key).SequenceEqual(expectOrder, StringComparer.Ordinal),
                    $"{window.FileGroupReport}（预期 {string.Join(" → ", expectOrder.Select(k => k.Length == 0 ? "无日期" : k))}）");

                check("各组条数之和等于文件总数",
                    groups.Sum(g => g.Count) == all.Count,
                    $"分组合计 {groups.Sum(g => g.Count)}，实际文件 {all.Count}");

                check("每组的条数与该日期的文件数一致",
                    groups.All(g => g.Count == all.Count(f => GroupKey(f) == g.Key)),
                    window.FileGroupReport);

                check("每个栏头都有可读文案",
                    groups.All(g => DateGrouping.Label(g.Key).Contains('·') ||
                                    DateGrouping.Label(g.Key) == "未标注日期"),
                    string.Join(" | ", groups.Select(g => DateGrouping.Label(g.Key))));

                // 光有分组对象不代表界面生成了栏头 —— GroupStyle 没生效就还是平铺。
                // 数一数可视树里真实的 GroupItem。
                var groupItems = Descendants(window.FindName("FileList") as DependencyObject)
                    .OfType<GroupItem>().ToList();
                check("界面上真的渲染出了栏头",
                    groupItems.Count == groups.Count,
                    $"可视树里 {groupItems.Count} 个栏头，分组对象 {groups.Count} 个");

                // 有 GroupItem 也不代表文字看得见：模板写错、宽度塌掉、被裁切都会
                // 让栏头变成一个空条。所以要求每个栏头的标题文字都有真实宽高。
                var headers = groupItems.Select(HeaderTexts).ToList();
                check("每个栏头都渲染出了有宽高的标题文字",
                    headers.Count > 0 && headers.All(hs =>
                        hs.Any(tb => tb.ActualWidth > 20 && tb.ActualHeight > 8)),
                    string.Join(" | ", headers.Select(hs =>
                        string.Join("+", hs.Select(tb => $"{tb.Text}[{tb.ActualWidth:0}×{tb.ActualHeight:0}]")))));

                check("每个栏头都标注了文件数",
                    headers.All(hs => hs.Any(tb => tb.Text.Contains("个文件", StringComparison.Ordinal))),
                    string.Join(" | ", headers.Select(hs => string.Join("+", hs.Select(tb => tb.Text)))));
            }

            // 切回只看当天，栏头应当消失
            window.SetFileFilter(false);
            window.UpdateLayout();
            check("切回只看当天后栏头消失", window.FileGroupCount == 0, window.FileGroupReport);
        }
        catch (Exception ex)
        {
            check("日期分组", false, ex.Message);
        }
        finally
        {
            try { window?.Close(); }
            catch { /* 关不掉不影响结论 */ }
        }
    }

    /// <summary>
    /// 月历面板的几何与交互断言。
    ///
    /// 重点是网格起点：以周一为第一列时，2026-09-01 是周二，网格左上角必须是
    /// 2026-08-31（周一）。这类偏移算错一位，整月日期就全部错位，而且界面看上去是"正常"的。
    /// </summary>
    private static void ProbeMonthCalendar(CheckFn check)
    {
        try
        {
            var today = DateTime.Today;
            var month = new DateTime(2026, 9, 1);          // 2026-09-01 是周二
            var marked = new[] { "2026-09-03", "2026-09-11" };

            var panel = new MonthCalendarPanel();
            panel.Show(month, new DateTime(2026, 9, 11), marked);

            var dates = panel.VisibleDates;
            check("固定 6 行 × 7 列 = 42 格", dates.Count == 42, $"{dates.Count} 格");
            check("显示月份正确", panel.DisplayMonth == month, panel.DisplayMonth.ToString("yyyy-MM-dd"));

            check("网格左上角是所在周的周一",
                dates[0] == new DateTime(2026, 8, 31) && dates[0].DayOfWeek == DayOfWeek.Monday,
                dates[0].ToString("yyyy-MM-dd dddd"));

            check("42 天连续无重复",
                dates.Select((d, i) => d == dates[0].AddDays(i)).All(x => x),
                $"{dates[0]:MM-dd} → {dates[^1]:MM-dd}");

            check("当月每一天各出现一次",
                Enumerable.Range(1, 30).All(day => dates.Count(d => d.Month == 9 && d.Day == day) == 1),
                "9 月 30 天");

            check("有记录的日期被标记", panel.MarkedCount == 2, $"{panel.MarkedCount} 天");

            // 翻月
            panel.GoMonth(-1);
            check("往前翻一个月", panel.DisplayMonth == new DateTime(2026, 8, 1),
                panel.DisplayMonth.ToString("yyyy-MM-dd"));
            check("翻月后网格仍从周一开始", panel.VisibleDates[0].DayOfWeek == DayOfWeek.Monday,
                panel.VisibleDates[0].ToString("yyyy-MM-dd dddd"));

            panel.GoMonth(1);
            check("翻回原月份", panel.DisplayMonth == month, panel.DisplayMonth.ToString("yyyy-MM-dd"));

            // 未来月份不允许翻：这里以面板所在月份为准，翻到"当前月"应禁用下月按钮
            panel.Show(new DateTime(today.Year, today.Month, 1), today, null);
            check("当前月不允许往后翻", !panel.CanGoNextMonth);
            panel.GoMonth(-1);
            check("历史月份允许往后翻", panel.CanGoNextMonth);

            // 选中
            DateTime? picked = null;
            panel.DateChosen += d => picked = d;

            panel.Show(month, new DateTime(2026, 9, 11), marked);
            var ok = panel.Pick(new DateTime(2026, 9, 5));
            check("选中历史日期成功", ok && picked == new DateTime(2026, 9, 5),
                picked?.ToString("yyyy-MM-dd") ?? "未触发");
            check("选中后面板选中态同步", panel.SelectedDate == new DateTime(2026, 9, 5),
                panel.SelectedDate.ToString("yyyy-MM-dd"));

            picked = null;
            var futureOk = panel.Pick(today.AddDays(1));
            check("未来日期不可选", !futureOk && picked == null);

            // 空数据不应抛异常
            panel.Show(month, month, null);
            check("没有记录时也能正常渲染", panel.MarkedCount == 0 && panel.VisibleDates.Count == 42);
        }
        catch (Exception ex)
        {
            check("月历面板", false, ex.Message);
        }
    }

    /// <summary>
    /// 「跳到」输入框的日期解析。
    ///
    /// 这里全是纯函数逻辑，断言起来最划算：跨年回退、闰年、无效日期、未来日期拒绝
    /// 这些分支在界面上极难逐一试到。
    /// </summary>
    private static void ProbeJumpParser(CheckFn check)
    {
        try
        {
            var today = DateTime.Today;
            var thisMonth = new DateTime(today.Year, today.Month, 1);

            DateTime Ok(string input, DateTime display)
            {
                var ok = MonthCalendarPanel.TryParseJump(input, display, out var date, out var err);
                check($"「{input}」解析成功", ok, ok ? date.ToString("yyyy-MM-dd") : err);
                return date;
            }

            void Bad(string input, string expectedHint, DateTime? display = null)
            {
                var ok = MonthCalendarPanel.TryParseJump(input, display ?? thisMonth, out _, out var err);
                check($"「{input}」被拒绝并给出提示", !ok && err.Contains(expectedHint), err.Length == 0 ? "（没给提示）" : err);
            }

            // 各种分隔符和写法都要认得
            var target = today.AddDays(-5);
            var sep = $"{target.Month}/{target.Day}";
            check("斜杠写法", Ok(sep, thisMonth) == target.Date, sep);
            check("横杠写法", Ok($"{target.Month}-{target.Day}", thisMonth) == target.Date);
            check("点号写法", Ok($"{target.Month}.{target.Day}", thisMonth) == target.Date);
            check("中文写法「9月5日」",
                Ok($"{target.Month}月{target.Day}日", thisMonth) == target.Date);
            check("中文写法带年份",
                Ok($"{target.Year}年{target.Month}月{target.Day}日", thisMonth) == target.Date);
            check("紧凑 4 位 MMdd",
                Ok($"{target.Month:00}{target.Day:00}", thisMonth) == target.Date);
            check("完整日期",
                Ok($"{target.Year}-{target.Month:00}-{target.Day:00}", thisMonth) == target.Date);
            check("紧凑 8 位 yyyyMMdd",
                Ok($"{target.Year}{target.Month:00}{target.Day:00}", thisMonth) == target.Date);

            // 关键词
            check("「昨天」", Ok("昨天", thisMonth) == today.AddDays(-1));
            check("「今天」", Ok("今天", thisMonth) == today);
            check("「前天」", Ok("前天", thisMonth) == today.AddDays(-2));

            // 只填日号 = 当前显示的那个月
            var bare = today.Day > 1 ? today.Day - 1 : 1;
            check("只填日号落在当前显示的月份",
                Ok(bare.ToString(), thisMonth) == new DateTime(today.Year, today.Month, bare));
            var lastMonth = thisMonth.AddMonths(-1);
            check("换个显示月份，日号跟着走",
                Ok("3", lastMonth) == new DateTime(lastMonth.Year, lastMonth.Month, 3));

            // 跨年回退：这是最容易被用户误解的一条，必须有断言守着
            var tomorrow = today.AddDays(1);
            if (tomorrow.Year == today.Year)
            {
                var input = $"{tomorrow.Month}/{tomorrow.Day}";
                var expected = new DateTime(today.Year - 1, tomorrow.Month, tomorrow.Day);
                check($"「{input}」今年还没到 → 按去年同一天理解",
                    Ok(input, thisMonth) == expected, expected.ToString("yyyy-MM-dd"));
            }

            var past = today.AddDays(-10);
            if (past.Year == today.Year)
            {
                check($"「{past.Month}/{past.Day}」今年已过 → 就是今年",
                    Ok($"{past.Month}/{past.Day}", thisMonth) == past.Date, past.ToString("yyyy-MM-dd"));
            }

            // 该拒绝的要拒绝
            Bad("13/5", "13 月");
            Bad("2/30", "这一天");
            Bad("abc", "看不懂");
            Bad("0/5", "0 月");

            // 下个月 1 号一定是未来 → 只填日号时应当报"还没到"
            var nextMonth = thisMonth.AddMonths(1);
            Bad("1", "还没到", nextMonth);

            // 空输入：返回 false 且不给报错（界面据此保持安静）
            var emptyOk = MonthCalendarPanel.TryParseJump("   ", thisMonth, out _, out var emptyErr);
            check("空输入不报错", !emptyOk && emptyErr.Length == 0);
        }
        catch (Exception ex)
        {
            check("日期跳转解析", false, ex.Message);
        }
    }

    private static uint ParseMods(string combo)
        => HotKeyService.TryParse(combo, out var mods, out _) ? mods : 0;

    private static uint ParseKey(string combo)
        => HotKeyService.TryParse(combo, out _, out var vk) ? vk : 0;

    /// <summary>
    /// 真的创建并显示一次窗口，然后关掉。
    ///
    /// 显示到屏幕外，避免自检时窗口在用户面前乱闪；悬浮窗比较特殊 ——
    /// 它 Loaded 时会按设置恢复自己的位置，所以先把位置临时挪到屏幕外再还原。
    /// </summary>
    private static void ProbeWindow(
        StringBuilder sb,
        CheckFn check,
        string name,
        Func<Window> factory,
        bool parkFloating = false)
    {
        double savedLeft = 0, savedTop = 0;
        bool movedFloating = false;

        try
        {
            if (parkFloating)
            {
                savedLeft = App.Settings.Settings.FloatingLeft;
                savedTop = App.Settings.Settings.FloatingTop;
                App.Settings.Settings.FloatingLeft = -32000;
                App.Settings.Settings.FloatingTop = -32000;
                movedFloating = true;
            }

            var window = factory();
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.ShowInTaskbar = false;
            window.Left = -32000;
            window.Top = -32000;

            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            window.Close();

            check($"{name}可正常创建", true);
        }
        catch (Exception ex)
        {
            check($"{name}可正常创建", false, ex.Message);
            sb.AppendLine($"      {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (movedFloating)
            {
                App.Settings.Settings.FloatingLeft = savedLeft;
                App.Settings.Settings.FloatingTop = savedTop;
            }
        }
    }

    /// <summary>跑自检并把结果写到 logs\selfcheck.log，返回日志路径，content 里是全文。</summary>
    public static string WriteReport(out string content)
    {
        var path = Path.Combine(AppPaths.LogDir, "selfcheck.log");
        content = RunReport();
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(path, content, new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            content = "自检本身失败：" + ex;
            try
            {
                File.WriteAllText(path, content, new UTF8Encoding(true));
            }
            catch
            {
                // 连日志都写不了就只能返回路径给调用方提示
            }
        }
        return path;
    }
}
