using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

        // 上面那节测"能不能切过去"，这节测"切过去之后显示得对不对"。
        // 两回事：切换链路全通、形象也真的换了，但显示框算错 —— 那就变成
        // "新形象被裁了/大小不对"，也就是 2026-09-20 用户报的那个。
        Section("形象显示几何");
        ProbeCharacterGeometry(Check, sb);

        // 上面那节测的是"解析函数对不对"，这节测的是"设置页上那一排按钮点下去有没有用"。
        // 两回事：解析全对但按钮是灰的，用户看到的仍然是"点新形象没反应"。
        ProbeCharacterChipsPage(Check, sb);

        // ---- 悬浮窗接收文件拖放 ----
        // 用户要的是"把文件拖到小鲸鱼上，自动打开主面板并归档"。
        // 这里验拖入之后的全部动作；"系统有没有把拖放交到这个窗口上"那一段
        // 造不出 DragEventArgs（构造函数是 internal），交给 tools/drop_probe.py
        // 用真实的 DoDragDrop 验。
        Section("悬浮窗的文件拖放");
        ProbeFloatingDrop(Check, sb);

        // 状态条右上角那两颗数字角标。失效方式全是安静的：
        // 数字跟旁边的文字对不上、收起态和展开态各显示一个数、
        // 角标跑到左半边或者探出窗口被裁掉 —— 都不报错，只是"看着不对"。
        Section("状态条的数字角标");
        ProbeFloatingBadges(Check, sb);

        // 上面那节验的是"文件进没进归档"，这节验的是"归档之后、以及一天干完之后
        // 小鲸鱼怎么回应"：归档 → 清点归档（角色 archive），到点下班 / 今天最后一条
        // 收尾 → 雀跃庆祝（角色 cheer）。失效方式全是安静的 —— 前缀写错 = 素材被当成
        // 普通闲置动作、两个角色串了 = 归档完跳起来庆祝、完工判定漏了基准判断 =
        // 启动瞬间或半夜零点蹦一下。三种都只是"看着别扭"，日志里一个字都没有。
        Section("归档与完工的互动");
        ProbeArchiveAndCheer(Check, sb);

        // 闲置小动作以前是 93 个等概率随机，现在按"今天干得怎么样"加权。
        // 失效方式同样是安静的：素材改名 → hint 一条都匹配不上 → 悄悄退回纯随机，
        // 界面照常出动作，你只是再也看不出它跟你的工作有什么关系了。
        Section("闲置动作：按工作状态挑选");
        ProbeAmbientMood(Check, sb);

        // ---- 透明视频 ----
        // 这条链路跨进程（ffmpeg 是外部程序），断了的表现是"点下去什么都不发生"。
        Section("透明视频动画");
        ProbeVideoPlayback(Check, sb);

        // 上面那节测的是"动画能不能播"，这节测的是"播出来的画面对不对"。
        // 两回事：帧数全对、一个错都不报，但 alpha 全丢（角色顶黑底）、
        // 或者红蓝互换（头发变栗棕）—— 两种都只有肉眼才看得见，所以这里直接验像素。
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
    /// 把月历面板单独渲染成 PNG（按整数倍放大，默认 4 倍）。
    ///
    /// **为什么它需要一条独立出口**：月历挂在 Popup 里，而 Popup 是独立的 HWND，
    /// <see cref="WritePageShot"/> 拍主窗口根本抓不到它 —— 在这之前，月历是整个界面里
    /// 唯一没有看图核销手段的一块。
    ///
    /// 而它恰恰是最需要看图的：格子里叠了"日期数字 + 有记录的小圆点 + 今天描边"三层，
    /// "圆点压住数字"是零点几个像素的事，1:1 渲染出来圆点只有 5px，凭肉眼判不了对错。
    /// 按整数倍放大再栅格化（矢量重绘，不是位图拉伸），差一个像素也能看出来。
    ///
    /// 面板是**新建一个实例**渲染的，不依赖 Popup 开没开、也不会弹到用户面前；
    /// 但圆点数据取真实数据目录，判定和界面上走的是同一处
    /// （<see cref="MainWindow.CollectMarkedDates"/>）。
    /// </summary>
    public static string WriteCalendarShot(string path, int scale = 4)
    {
        if (scale < 1) scale = 1;

        // 固定看今天所在的月份：有记录的日期、今天描边、选中态三种情况都在这一屏里，
        // 不用为了"看某个月的圆点"去翻月 —— 翻月是交互，不是渲染的事。
        var today = DateTime.Today;

        var panel = new MonthCalendarPanel();
        panel.Show(today, today, MainWindow.CollectMarkedDates());

        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = panel.DesiredSize;
        panel.Arrange(new Rect(new Point(0, 0), size));
        panel.UpdateLayout();

        int pw = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
        int ph = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
        double dpi = 96.0 * scale;

        var panelBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            pw, ph, dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32);
        panelBitmap.Render(panel);

        // 面板本身是透明的（圆角卡片外那圈 Margin 是空的），垫一层窗口底色，
        // 否则看图分不清卡片边界，也没法判断"圆点是不是贴到格子边缘了"。
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = Application.Current?.TryFindResource("WindowBackgroundBrush")
                         as System.Windows.Media.Brush
                     ?? System.Windows.Media.Brushes.White;
            dc.DrawRectangle(bg, null, new Rect(0, 0, size.Width, size.Height));
            dc.DrawImage(panelBitmap, new Rect(0, 0, size.Width, size.Height));
        }

        var composed = new System.Windows.Media.Imaging.RenderTargetBitmap(
            pw, ph, dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32);
        composed.Render(visual);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(composed));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs2 = File.Create(path);
        encoder.Save(fs2);

        DumpCalendarGeometry(panel);
        return path;
    }

    /// <summary>
    /// 把月历的布局几何打到 stdout。
    ///
    /// 存在的理由：月历"格子被裁"这种缺陷**不报任何错、也不影响任何断言**，
    /// 只有选中格（有实心背景）才看得出来，而 1:1 下就是几个像素的直角。
    /// 2026-09-24 用户报的"当天日期圆角残缺"就是这么来的 ——
    /// 面板宽度算错 → 列宽 34.57 装不下 36+2×1 的格子 → WPF 给 Button 套了
    /// 矩形 layout clip，右侧 3.43 DIP 连圆角一起切平。
    /// 所以截图的同时把几何念出来，"图上看不对"和"数字印证"就在同一条输出里。
    /// </summary>
    private static void DumpCalendarGeometry(MonthCalendarPanel panel)
    {
        if (panel.FindName("DayGrid") is not UniformGrid grid)
        {
            Console.WriteLine("[geom] 找不到 DayGrid");
            return;
        }

        double colWidth = grid.ActualWidth / grid.Columns;
        int clipped = 0;
        double firstClipWidth = 0, cellSide = 0;

        foreach (var child in grid.Children)
        {
            if (child is not FrameworkElement el) continue;
            if (cellSide == 0) cellSide = el.ActualWidth;
            var clip = System.Windows.Media.VisualTreeHelper.GetClip(el);
            if (clip == null) continue;
            if (clipped++ == 0) firstClipWidth = clip.Bounds.Width;
        }

        Console.WriteLine($"[geom] 面板 {panel.ActualWidth:F1}×{panel.ActualHeight:F1} · "
                          + $"DayGrid {grid.ActualWidth:F1}×{grid.ActualHeight:F1} · "
                          + $"列宽 {colWidth:F2} · 格子 {cellSide:F1}×{cellSide:F1} "
                          + $"(布局占 {cellSide + 2:F1})");

        Console.WriteLine(clipped == 0
            ? "[geom] 无裁剪：列宽装得下格子"
            : $"[geom] ⚠ {clipped}/{grid.Children.Count} 格被布局裁剪，"
              + $"可见宽 {firstClipWidth:F2}（需要 {cellSide + 2:F1}）"
              + " —— 圆角会被切平成直角，加宽面板或缩小格子");
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

            // 设置页是可滚动的长页面，两个滑杆都排在靠下的位置。
            // 探针窗口按默认高度装不下整页时，滑杆会被底部的状态栏压住，
            // 命中测试打上去得到 StatusBar —— 那是"没滚到"，不是"被挡住了"。
            // 把探针窗口临时撑高让整页摊开（窗口在屏幕外，撑多高都不碍事）。
            window.Height = 1400;
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

                // 期望值取窗口自己声明的设计宽 —— 它随形象包变（原版 236、dsh 391），
                // 写死 236 的话换个形象就误报"窗口尺寸没跟着变"。
                double designW = window.DesignSize.W;

                check($"缩放 {scale:0.0} 时窗口尺寸跟着变",
                    Math.Abs(window.Width - Math.Round(designW * scale)) < 1.5,
                    $"窗口宽 {window.Width:0}，期望 {Math.Round(designW * scale):0}" +
                    $"（版式宽 {designW:0.#}，显示框 {window.WhaleWidth:0.#}×{window.WhaleHeight:0.#}）");

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
                Math.Abs(window.Width - Math.Round(window.DesignSize.W * 1.4)) < 1.5,
                $"窗口宽 {window.Width:0}，期望 {Math.Round(window.DesignSize.W * 1.4):0}");

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
                $"实际 {window.WhaleHeight:0.#}px（原版静态形象是 167.8px）");

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
    /// 形象显示几何：**两套形象各按自己的画布算显示框**。
    ///
    /// 这是 2026-09-20 用户报的「两套动画模板冲突、新版显示会被裁」的回归断言。
    /// 当时是两个毛病叠在一起，两个都要钉住：
    ///
    /// 1. **共用一把尺子**：显示框高写死 168，两套画布共用。
    ///    dsh（384 画布、角色 270）被显示成 116.8px，一开动画又跳回 165px ——
    ///    每播一次动作形象放大一倍再缩回去。这条钉在"显示框由各自画布算出"。
    /// 2. **把 16:9 挤成方的**：宽度只能由高度推，等于假定画布是方的。
    ///    16:9 的素材要显示就只能先 crop 成方格，道具被切成平口。
    ///    这条钉在"显示框的宽高比 == 画布的宽高比"—— 挤扁了就对不上。
    ///
    /// 顺带把兜底也钉一遍：几何文件读不出来只该"显示得和原版一样大"，
    /// 不该让悬浮窗起不来，更不该抛。
    /// </summary>
    private static void ProbeCharacterGeometry(CheckFn check, StringBuilder sb)
    {
        // ---- 原版：一个像素都不许动 ----
        var classic = CharacterGeometry.Classic;
        var (cw, ch) = classic.DisplayBox();
        check("原版几何：610 画布、角色 600 → 显示框 167.75",
            Math.Abs(cw - 167.75) < 0.01 && Math.Abs(ch - 167.75) < 0.01,
            $"实际 {cw:0.##}×{ch:0.##}");

        var (lw, lh) = classic.LayoutSize();
        check("原版版式还是 236×296（换算法不许动到它）",
            Math.Abs(lw - 236) < 0.01 && Math.Abs(lh - 296) < 0.01,
            $"实际 {lw:0.##}×{lh:0.##}");

        // ---- 真实素材：16:9 那套 ----
        var dshRoot = CharacterPacks.DirOf(CharacterPacks.DshId);
        if (CharacterPacks.IsAvailable(CharacterPacks.DshId))
        {
            var dsh = CharacterGeometry.Resolve(dshRoot);
            sb.AppendLine("  " + dsh.Describe());

            var (bw, bh) = dsh.DisplayBox();
            check("dsh 的显示框按它自己的画布算（不再套原版的 168）",
                Math.Abs(bh - 220) < 1.5 && Math.Abs(bw - 391.1) < 2.0,
                $"实际 {bw:0.#}×{bh:0.#}，期望 391.1×220");

            // ★ 这条就是"新版被裁"的根：显示框必须和画布同比例。
            //   宽度只能从高度推的话，16:9 会被显示成方的，多的部分就得裁掉。
            double canvasAspect = (double)dsh.CanvasWidth / dsh.CanvasHeight;
            double boxAspect = bw / bh;
            check("显示框和画布同比例（16:9 不会被挤成方的 → 道具不被裁）",
                Math.Abs(boxAspect - canvasAspect) < 0.01,
                $"画布 {dsh.CanvasWidth}×{dsh.CanvasHeight}（比 {canvasAspect:0.###}），" +
                $"显示框 {bw:0.#}×{bh:0.#}（比 {boxAspect:0.###}）");

            var (dw, dh) = dsh.LayoutSize();
            check("窗口装得下 dsh 的显示框（装不下就会被裁）",
                dw >= bw - 0.01 && dh >= bh + CharacterGeometry.VerticalPadding - 0.01,
                $"窗口 {dw:0.#}×{dh:0.#}，显示框 {bw:0.#}×{bh:0.#}");

            // ---- 帧表的画布必须和 index.json 一致 ----
            // 两处对不上的表现是"静止一张大小、播起来另一张大小"，
            // 也就是用户最早说的"两套模板冲突"。素材换过一轮之后最容易忘掉一处。
            var lib = AnimLibrary.Scan(dshRoot);
            var fromSheets = lib.DisplayBox(CharacterGeometry.DesignCharacterHeight);
            if (fromSheets.HasValue)
            {
                check("帧表算出的显示框和 index.json 的一致（静止↔播放不跳大小）",
                    Math.Abs(fromSheets.Value.Width - bw) < 0.5 &&
                    Math.Abs(fromSheets.Value.Height - bh) < 0.5,
                    $"帧表 {fromSheets.Value.Width:0.#}×{fromSheets.Value.Height:0.#} " +
                    $"vs 几何 {bw:0.#}×{bh:0.#}");
            }
        }
        else
        {
            sb.AppendLine("  （没装新版形象包，跳过它的几何断言）");
        }

        // ---- 兜底：坏几何一律退回原版，绝不抛 ----
        var bad = new (string Json, string Why)[]
        {
            ("", "空文件"),
            ("{", "不是合法 JSON"),
            ("{}", "没有 geometry 段"),
            ("{\"geometry\":{}}", "geometry 是空的"),
            ("{\"geometry\":{\"canvasWidth\":0,\"canvasHeight\":360,\"characterHeight\":270}}", "画布宽为 0"),
            ("{\"geometry\":{\"cell\":0}}", "老写法的 cell 也是 0"),
            ("{\"geometry\":{\"canvasWidth\":99999,\"canvasHeight\":360,\"characterHeight\":270}}", "画布大得离谱"),
        };

        int fellBack = 0;
        foreach (var (json, why) in bad)
        {
            var g = CharacterGeometry.Parse(json, why);
            if (ReferenceEquals(g, CharacterGeometry.Classic)) fellBack++;
        }
        check("坏几何一律退回原版（不抛、也不是空白）",
            fellBack == bad.Length, $"{fellBack}/{bad.Length} 项退了回去");

        // 老包的 cell 字段也得认 —— 否则升级一次就得让所有人重新生成素材
        var legacy = CharacterGeometry.Parse(
            "{\"geometry\":{\"cell\":384,\"characterHeight\":270}}", "老写法");
        check("老包的 cell 字段仍被接受（当成方形画布）",
            legacy.CanvasWidth == 384 && legacy.CanvasHeight == 384 && legacy.CharacterHeight == 270,
            $"实际 {legacy.CanvasWidth}×{legacy.CanvasHeight}、角色 {legacy.CharacterHeight}");
    }

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
    /// 自检整体是同步的、跑在 UI 线程上，而界面里有些流程是 async。
    /// 在这里 .Wait() 会把 UI 线程堵死，await 的续体永远排不上 —— 直接死锁。
    /// 所以要一边推 Dispatcher 的消息队列，一边等任务自己完成。
    /// </summary>
    private static bool PumpUntilComplete(Task task, int timeoutMs = 20000)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var deadline = Environment.TickCount64 + timeoutMs;

        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            DispatcherPriority.Background,
            (_, _) =>
            {
                if (task.IsCompleted || Environment.TickCount64 > deadline) frame.Continue = false;
            },
            dispatcher);

        // 完成时立刻收手，不用等下一次定时器 tick
        task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));

        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }

        return task.IsCompleted;
    }

    /// <summary>
    /// 把文件拖到桌面小鲸鱼上，能不能一路走到"归档 + 打开主面板"。
    ///
    /// 这一节覆盖的边界是**从"窗口拿到路径"往后**：OLE 事件那一段
    /// （系统有没有把拖放交到这个窗口上）造不出 DragEventArgs，测不了 ——
    /// 那段由 tools/drop_probe.py 用真实的 DoDragDrop 验（三个场景，含对照组）。
    /// 但"接住之后该发生的事有没有发生"才是功能的主体，必须断言；
    /// 否则就会停在前几轮那种"事件收到了、后面什么都没做"的状态上。
    /// </summary>
    private static void ProbeFloatingDrop(CheckFn check, StringBuilder sb)
    {
        var previousAutoOpen = App.Settings.Settings.AutoOpenMainWindowOnDrop;

        // 待归档的测试文件必须放在数据目录**外面**：归档会主动跳过自己数据目录里的文件
        // （免得把 index.json、快照副本又拖回来归档），放 logs\ 下就一个都收不上，
        // 而且表现只是"没有可归档的文件"，看不出是被过滤了。
        var probeRoot = Path.Combine(Path.GetTempPath(),
            "whalelog-drop-probe-" + Guid.NewGuid().ToString("N")[..8]);
        var app = (App)Application.Current;
        var made = new List<string>();

        FloatingWindow? floating = null;

        try
        {
            Directory.CreateDirectory(probeRoot);

            // 造两个真文件。路径里带中文，顺便验一遍非 ASCII 不会在路上出问题。
            for (int i = 1; i <= 2; i++)
            {
                var f = Path.Combine(probeRoot, $"季报 {i}.txt");
                File.WriteAllText(f, $"自检造的第 {i} 个文件\n");
                made.Add(f);
            }

            // 探针验的就是"按设置打开主面板"这条路
            App.Settings.Update(s => s.AutoOpenMainWindowOnDrop = true);

            floating = new FloatingWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Left = Offscreen,
                Top = Offscreen,
                BackgroundProbeMode = true,      // 待在屏幕外，别打扰用户
            };
            floating.Show();
            floating.UpdateLayout();

            // 拖进来的时候提示层要亮出来 —— "一声不吭地收下"和"没接住"是分不清的
            floating.ShowDropHintForProbe(2);
            check("拖入过程中会亮出落点提示",
                floating.DropHintVisible && floating.DropHintLabel.Contains('2'),
                $"可见={floating.DropHintVisible}，文案=「{floating.DropHintLabel.Replace(Environment.NewLine, " ")}」");

            floating.HideDropHintForProbe();
            check("松手之后落点提示会收掉", !floating.DropHintVisible,
                floating.DropHintVisible ? "还亮着" : "已收掉");

            int before = App.Store.Book.Files.Count;
            var today = DateTime.Today.ToString("yyyy-MM-dd");

            // 先把主面板支起来、并翻到昨天 —— 用户上次可能正好停在别的日期上。
            // 悬浮窗拖入必须记到今天，跟主面板当时在看哪天无关；
            // 不先翻走的话，主面板恰好就在今天，这条规则对不对根本测不出来。
            var yesterday = DateTime.Today.AddDays(-1);
            var opening = app.ShowMainWindowForDropAsync();
            if (PumpUntilComplete(opening, 10000)) opening.Result?.SetViewingDateForProbe(yesterday);
            sb.AppendLine($"  拖入前主面板正在看：{app.MainWindowForProbe?.ViewingDate:yyyy-MM-dd}");

            var task = floating.AcceptDropAsync(made.ToArray());
            bool finished = PumpUntilComplete(task, 20000);

            check("整条拖入流程跑完了（没卡在半路）", finished, finished ? "已返回" : "超时未返回");

            var summary = finished ? task.Result : "";
            sb.AppendLine($"  悬浮窗会弹的气泡：{summary}");

            var main = app.MainWindowForProbe;

            check("主面板被打开了", main is { IsVisible: true },
                main == null ? "主面板没创建" : $"IsVisible={main.IsVisible}");

            check("主面板切到了「文件归档」页",
                main?.ActivePage == NavPage.Files,
                main == null ? "主面板没创建" : $"当前页={main.ActivePage}");

            var mine = App.Store.Book.Files
                .Where(f => made.Contains(f.FullPath, StringComparer.OrdinalIgnoreCase))
                .ToList();

            check("拖进去的文件真的进了归档", mine.Count == 2,
                $"归档前 {before} 条 → 现在 {App.Store.Book.Files.Count} 条，其中本次 {mine.Count} 条");

            check("归属日期是今天（不是上次翻到的那一天）",
                mine.Count == 2 && mine.All(f => f.AddedDate == today),
                mine.Count == 0
                    ? "一条都没找到"
                    : string.Join("、", mine.Select(f => $"{f.FileName}={f.AddedDate}")));

            check("主面板跟着跳回了今天（不然用户会以为文件丢了）",
                main?.ViewingDate.Date == DateTime.Today,
                $"拖入前看的是 {yesterday:yyyy-MM-dd}，现在是 {main?.ViewingDate:yyyy-MM-dd}");

            check("结果被报成了人话（含日期口径 + 数量）",
                summary.Contains("已记入今天") && summary.Contains("新增"), $"「{summary}」");

            // 归档成功后小鲸鱼要演一段"清点归档"（见 AnimActions.Archive）。
            // 形象包里没有这个角色时（原版那套就没有）"不演"才是对的 ——
            // 所以先看这个包里有没有清点素材，有就必须已经在播它。
            // 素材齐全时的完整判定（含"重复文件不演""完工才庆祝"）由【归档与完工的互动】
            // 那节用最小素材验。
            check("归档成功后演了清点归档（这个形象包有素材时）",
                floating.ArchiveActionCount == 0 || floating.CurrentAnimation == floating.FirstArchiveAction,
                $"清点归档素材 {floating.ArchiveActionCount} 个，" +
                $"此刻在播 {floating.CurrentAnimation ?? "（无）"}");

            // ---- 另一半：关掉「自动打开主面板」之后，应该安静地收下 ----
            //
            // 这个开关以前只掐掉半个动作（面板照样弹出来、只是没切页），和它的名字对不上。
            // 现在它是"要不要把主面板叫出来"的总闸，所以必须两边都验：
            // 开了 → 面板出现；关了 → 面板**根本不创建**，但文件照样进归档。
            // 只验"开了"那一半的话，"关掉"变成"什么都不做"也照样全绿。
            app.CloseMainWindowForProbe();
            sb.AppendLine($"  关掉开关前先收起主面板：MainWindowForProbe={(app.MainWindowForProbe == null ? "null" : "还在")}");

            App.Settings.Update(s => s.AutoOpenMainWindowOnDrop = false);

            var quietFiles = new List<string>();
            for (int i = 3; i <= 4; i++)
            {
                var f = Path.Combine(probeRoot, $"季报 {i}.txt");
                File.WriteAllText(f, $"自检造的第 {i} 个文件\n");
                quietFiles.Add(f);
                made.Add(f);
            }

            int beforeQuiet = App.Store.Book.Files.Count;
            var quietTask = floating.AcceptDropAsync(quietFiles.ToArray());
            bool quietDone = PumpUntilComplete(quietTask, 20000);

            check("关掉开关后拖入也跑完了（没卡在半路）", quietDone, quietDone ? "已返回" : "超时未返回");

            var quietSummary = quietDone ? quietTask.Result : "";
            sb.AppendLine($"  关掉开关后的气泡：{quietSummary}");

            var quietMine = App.Store.Book.Files
                .Where(f => quietFiles.Contains(f.FullPath, StringComparer.OrdinalIgnoreCase))
                .ToList();

            check("关掉开关后文件仍然进了归档（只是不打扰用户）",
                quietMine.Count == 2,
                $"归档前 {beforeQuiet} 条 → 现在 {App.Store.Book.Files.Count} 条，其中本次 {quietMine.Count} 条");

            check("关掉开关后归属日期还是今天",
                quietMine.Count == 2 && quietMine.All(f => f.AddedDate == today),
                quietMine.Count == 0 ? "一条都没找到" : string.Join("、", quietMine.Select(f => f.AddedDate)));

            check("关掉开关后主面板**没有**被创建出来（这才是这个开关的承诺）",
                app.MainWindowForProbe == null,
                app.MainWindowForProbe == null ? "没创建" : "被创建了 —— 开关只掐掉了切页那半步");

            check("关掉开关后的气泡和开着时是同一种说法（两条路只差要不要弹面板）",
                quietSummary.Contains("已记入今天") && quietSummary.Contains("新增"),
                $"「{quietSummary}」");

            App.Settings.Update(s => s.AutoOpenMainWindowOnDrop = previousAutoOpen);
        }
        catch (Exception ex)
        {
            check("悬浮窗的文件拖放", false, ex.Message);
            sb.AppendLine("      " + ex);
        }
        finally
        {
            try { floating?.Close(); } catch { /* 关不掉不影响结论 */ }
            try { app.CloseMainWindowForProbe(); } catch { }

            // 探针真的往库里写了归档记录。自检日志是给用户看的，
            // 但绝不能让用户自己的记录里多出两个「季报 1.txt」—— 原样撤回。
            try
            {
                var added = App.Store.Book.Files
                    .Where(f => made.Contains(f.FullPath, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                foreach (var f in added) App.Store.Book.Files.Remove(f);
                if (added.Count > 0) App.Store.Save();   // Save 会把当天那份 md 一并重写回去
            }
            catch { /* 撤不回去也只能继续 */ }

            try { App.Settings.Update(s => s.AutoOpenMainWindowOnDrop = previousAutoOpen); } catch { }
            try { Directory.Delete(probeRoot, true); } catch { }
        }
    }

    /// <summary>
    /// 状态条右上角的两颗数字角标（记录数 / 未收尾）。
    ///
    /// 这里**自己造几条今天的记录**再刷新，因为两颗角标得同时亮着才验得到：
    /// 造 3 条、其中 2 条收尾 → 记录数角标应是 3、未收尾角标应是 1。
    /// 两个数刻意取不一样的，"各管各的"才验得出来 —— 都写 3 的话，两个来源串了也看不出来。
    ///
    /// 为什么值得单独验：这一套坏掉的方式全是安静的 —— 数字跟旁边的文字对不上、
    /// 收起态和展开态各显示一个数、角标飘到左半边、探出窗口被裁掉、
    /// 或者把状态文字挤成省略号。程序一句错都不报，只有看图才发现。
    /// </summary>
    private static void ProbeFloatingBadges(CheckFn check, StringBuilder sb)
    {
        FloatingWindow? floating = null;
        var dayWasThere = App.Store.Book.FindDay(DateTime.Today) != null;
        DayNote? day = null;
        List<NoteEntry>? originalEntries = null;

        try
        {
            // ---- 造数据：今天的 3 条记录，前两条收尾、第三条留着 ----
            day = App.Store.GetOrCreateDay(DateTime.Today);
            originalEntries = day.Entries;
            day.Entries = new List<NoteEntry>();
            for (int i = 1; i <= 3; i++)
            {
                day.Entries.Add(new NoteEntry
                {
                    Text = $"自检造的角标记录 {i}",
                    CreatedAt = DateTime.Today.AddHours(9).AddMinutes(i),
                    Done = i <= 2,
                });
            }

            floating = new FloatingWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Left = Offscreen,
                Top = Offscreen,
                BackgroundProbeMode = true,      // 待在屏幕外，别打扰用户
            };
            floating.Show();
            floating.RefreshStatusForProbe();
            floating.UpdateLayout();

            var todayText = floating.TodayTextForProbe;
            sb.AppendLine($"  造了 3 条记录（其中 1 条未收尾）→ 状态条第一行：「{todayText}」");
            sb.AppendLine($"  角标：记录数「{floating.TodayBadgeLabelForProbe}」" +
                          $"（可见={floating.TodayBadgeVisibleForProbe}）、" +
                          $"未收尾「{floating.OpenBadgeLabelForProbe}」" +
                          $"（可见={floating.OpenBadgeVisibleForProbe}）");

            check("记录数角标跟着今天的条数走",
                floating.TodayBadgeVisibleForProbe && floating.TodayBadgeLabelForProbe == "3",
                $"可见={floating.TodayBadgeVisibleForProbe}，数字={floating.TodayBadgeLabelForProbe}（应为 3）");

            check("未收尾角标跟未收尾条数走（和记录数是两个数）",
                floating.OpenBadgeVisibleForProbe && floating.OpenBadgeLabelForProbe == "1",
                $"可见={floating.OpenBadgeVisibleForProbe}，数字={floating.OpenBadgeLabelForProbe}（应为 1）");

            check("记录数角标和状态条文字里那个数是同一个",
                ParseLeadingCount(todayText) == 3,
                $"从「{todayText}」里读出来的是 {ParseLeadingCount(todayText)}");

            check("收起态胶囊的角标和展开态那颗一致（同一个数不能有两个值）",
                floating.CollapsedBadgeVisibleForProbe == floating.OpenBadgeVisibleForProbe &&
                floating.CollapsedBadgeLabelForProbe == floating.OpenBadgeLabelForProbe,
                $"展开态「{floating.OpenBadgeLabelForProbe}」(可见={floating.OpenBadgeVisibleForProbe})、" +
                $"收起态「{floating.CollapsedBadgeLabelForProbe}」(可见={floating.CollapsedBadgeVisibleForProbe})");

            // ---- 位置：两颗角标要骑在状态条的右上角，而且还不能压住左边的文字 ----
            var geo = floating.BadgeGeometryForProbe;
            check("状态条与角标的位置量得出来（窗口已经布好版）", geo != null,
                geo == null ? "量不到" : $"状态条 {geo.Value.Bar}");

            if (geo is { } g && g.Badges is { } badges)
            {
                sb.AppendLine($"  状态条 {g.Bar}；文字块 {g.Texts}；角标组 {badges}");

                check("角标落在状态条的右半边（不是飘到左边）",
                    badges.Left > g.Bar.Left + g.Bar.Width / 2 && badges.Right >= g.Bar.Right - 14,
                    $"角标 {badges}，状态条 {g.Bar}");

                check("角标没压住左边的状态文字",
                    g.Texts.Right <= badges.Left + 0.5,
                    $"文字右沿 {g.Texts.Right:0.#}，角标左沿 {badges.Left:0.#}");

                check("角标没把状态文字挤到打省略号",
                    g.Texts.Width + 0.5 >= floating.TodayTextNaturalWidthForProbe,
                    $"文字不打省略号要 {floating.TodayTextNaturalWidthForProbe:0.#}，" +
                    $"实际只有 {g.Texts.Width:0.#} 可用");

                check("角标整颗都在窗口里（探出去会被窗口裁掉）",
                    badges.Left >= -0.5 && badges.Top >= -0.5 &&
                    badges.Right <= floating.ActualWidth + 0.5 &&
                    badges.Bottom <= floating.ActualHeight + 0.5,
                    $"角标 {badges}，窗口 {floating.ActualWidth:0.#}×{floating.ActualHeight:0.#}");
            }
            else
            {
                check("两颗角标都该亮着（造了 3 条、其中 1 条未收尾）", false,
                    geo == null ? "位置量不到" : "角标组没显示");
            }

            // ---- 反过来：今天一条都没有时，两颗角标都不该出现 ----
            day.Entries.Clear();
            floating.RefreshStatusForProbe();
            floating.UpdateLayout();

            check("今天一条都没有时角标全收起来（挂个 0 是噪音）",
                !floating.TodayBadgeVisibleForProbe && !floating.OpenBadgeVisibleForProbe,
                $"记录数可见={floating.TodayBadgeVisibleForProbe}、" +
                $"未收尾可见={floating.OpenBadgeVisibleForProbe}，" +
                $"状态条文字「{floating.TodayTextForProbe}」");
        }
        catch (Exception ex)
        {
            check("状态条的数字角标", false, ex.Message);
        }
        finally
        {
            // 数据一定要还原：这份 store 是内存里的真身，探针留下的条目会被后面的
            // 自检当成真数据看，还可能被下一次 Save 写进用户的书里。
            try
            {
                if (day != null)
                {
                    day.Entries = originalEntries ?? new List<NoteEntry>();
                    if (!dayWasThere) App.Store.Book.Days.Remove(day);
                }
            }
            catch { /* 还原不了也只能继续 */ }

            try { floating?.Close(); } catch { }
        }
    }

    /// <summary>从「3 条记录 · 0 个文件」里抠出开头那个数；没有记录时返回 0。</summary>
    private static int ParseLeadingCount(string text)
    {
        var head = text.Split(' ')[0];
        return int.TryParse(head, out var n) ? n : 0;
    }

    /// <summary>
    /// 归档 → 演"清点归档"（角色 archive）；完工 → 演"雀跃庆祝"（角色 cheer）。
    ///
    /// 这里自己造一份**最小素材**（archive + cheer + 一个点击动作）跑真实代码路径：
    /// 扫描 → 按角色归类 → 归档后起演 → 重复文件不演 → 最后一条收尾起演 →
    /// 跨零点／首刷／清空都不许演 → 空白天到点下班照演。
    /// 用精灵图而不是真视频，是因为要**确定性地推进时间轴**
    /// （<see cref="SpritePlayer.AdvanceBy"/>），不等显示器刷新率 —— 断言才不会时灵时不灵。
    ///
    /// 为什么必须真的走一遍：这些失效方式没有一个是会报错的 ——
    /// 前缀写错（素材被当成普通闲置动作，归档和下班时都什么都不演）、
    /// 两个角色串了（归档完跳起来庆祝、下班时低头清点）、
    /// 完工判定漏了基准判断（启动瞬间蹦一下，或者半夜零点"换天"时未收尾从 3 掉到 0 又蹦一下）。
    /// </summary>
    private static void ProbeArchiveAndCheer(CheckFn check, StringBuilder sb)
    {
        var probeRoot = Path.Combine(AppPaths.LogDir, "archive-cheer-probe");
        var assetDir = Path.Combine(probeRoot, "Assets");

        // 10fps × 2 帧 → 推 500ms 必定播完
        const int frameSize = 720;
        var archiveAction = AnimActions.Archive + "-探针清点";
        var cheerAction = AnimActions.Cheer + "-探针庆祝";

        string? previousAssetDir = null;
        FloatingWindow? window = null;
        var settings = App.Settings.Settings;
        var dayWasThere = App.Store.Book.FindDay(DateTime.Today) != null;
        DayNote? day = null;
        List<NoteEntry>? originalEntries = null;

        try
        {
            previousAssetDir = Environment.GetEnvironmentVariable("WHALELOG_ASSET_DIR");

            // 和别的悬浮窗探针同一个道理：位置挪到屏幕外、关掉自动收起
            // （收起会顺手 SavePosition 把坐标写进设置，外面那层快照兜底）
            settings.FloatingAnimations = true;
            settings.FloatingAutoCollapse = false;
            settings.FloatingLeft = Offscreen;
            settings.FloatingTop = Offscreen;

            WriteProbeAnimation(assetDir, archiveAction, frameSize, 2, 2);
            WriteProbeAnimation(assetDir, cheerAction, frameSize, 2, 2);
            WriteProbeAnimation(assetDir, AnimActions.Click, frameSize, 2, 2);

            // ---- 扫描层：两个角色各归各的 ----
            // 归错的表现是"闲下来随机蹦出清点归档"，而归档和下班时什么都不演。
            var lib = AnimLibrary.Scan(assetDir);
            var archive = lib.ActionsWithRole(AnimActions.Archive);
            var cheer = lib.ActionsWithRole(AnimActions.Cheer);
            var ambient = lib.ActionsWithRole(AnimActions.Ambient);

            check("清点归档被认成 archive 角色（不是普通闲置小动作）",
                archive.Count == 1 && archive[0] == archiveAction,
                $"认到 {archive.Count} 个；闲置小动作 {ambient.Count} 个");

            check("雀跃庆祝被认成 cheer 角色（和清点归档是两个角色）",
                cheer.Count == 1 && cheer[0] == cheerAction && !archive.Contains(cheerAction),
                $"认到 {cheer.Count} 个：{string.Join("、", cheer)}");

            // ---- 界面层 ----
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

            check("悬浮窗把两个角色分开记下来了",
                window.ArchiveActionCount == 1 && window.CheerActionCount == 1,
                $"清点归档 {window.ArchiveActionCount} 个（{window.FirstArchiveAction ?? "（无）"}）、" +
                $"完工庆祝 {window.CheerActionCount} 个（{window.FirstCheerAction ?? "（无）"}）");

            var player = window.AnimPlayer;
            if (player == null)
            {
                check("播放器已建立", false, "拿不到播放器");
                return;
            }

            // ---- 归档成功 → 演清点归档，且只有清点归档 ----
            check("归档成功后演的是清点归档",
                window.PlayArchiveCheckForProbe(accepted: 1) && window.CurrentAnimation == archiveAction,
                $"在播={window.CurrentAnimation ?? "（无）"}");

            player.AdvanceBy(TimeSpan.FromMilliseconds(500));

            check("清点归档演完就收手，不会自己接上雀跃庆祝",
                !window.IsAnimationPlaying && window.CurrentAnimation != cheerAction,
                $"在播={window.CurrentAnimation ?? "（无）"}");

            check("同一份文件再拖一次（没有新增）不演清点归档",
                !window.PlayArchiveCheckForProbe(accepted: 0) && !window.IsAnimationPlaying,
                $"在播={window.CurrentAnimation ?? "（无）"}");

            // ---- 今天最后一条未收尾被标掉 → 演雀跃庆祝 ----
            // 走真实的 RefreshStatus：造 2 条、其中 1 条没收尾，先刷一次量基准，
            // 标掉最后那条后再刷 —— 这正是用户在主面板勾完那一瞬间发生的事。
            day = App.Store.GetOrCreateDay(DateTime.Today);
            originalEntries = day.Entries;
            day.Entries = new List<NoteEntry>
            {
                new() { Text = "自检造的收尾记录", CreatedAt = DateTime.Today.AddHours(9), Done = true },
                new() { Text = "自检造的未收尾记录", CreatedAt = DateTime.Today.AddHours(10), Done = false },
            };

            window.ResetWorkFinishedBaselineForProbe();
            window.RefreshStatusForProbe();          // 量基准：还有 1 条没收尾

            var baseline = window.WorkFinishedBaselineForProbe;
            sb.AppendLine($"  造 2 条记录（1 条未收尾）→ 基准 = {baseline.Day}／未收尾 {baseline.Open}");

            check("刷新时把「今天／未收尾」记成了基准（不然判断不了「刚做完」）",
                baseline.Open == 1 && baseline.Day == DateTime.Today.ToString("yyyy-MM-dd"),
                $"基准 = 未收尾 {baseline.Open}、日期「{baseline.Day}」");

            check("还差一条没收尾时什么都不演",
                !window.IsAnimationPlaying,
                $"在播={window.CurrentAnimation ?? "（无）"}");

            day.Entries[1].Done = true;
            window.RefreshStatusForProbe();          // 最后一条收尾的那一刻

            check("最后一条收尾的那一刻演雀跃庆祝",
                window.CurrentAnimation == cheerAction,
                $"在播={window.CurrentAnimation ?? "（无）"}");

            player.AdvanceBy(TimeSpan.FromMilliseconds(500));
            window.RefreshStatusForProbe();

            check("再刷一次不会重复演（基准跟着数据走）",
                !window.IsAnimationPlaying,
                $"在播={window.CurrentAnimation ?? "（无）"}");

            // ---- 完工判定不该误演的三种情况 ----
            // 造得出假日期才验得了"跨零点"（真等跨零点没法测），所以直接喂判定。
            // 三种都只是"小鲸鱼莫名蹦一下"，日志里一个字都没有。
            window.ResetWorkFinishedBaselineForProbe();
            window.NoteWorkFinishedForProbe(DateTime.Today.AddDays(-1), entries: 3, open: 3);

            check("跨零点不演（昨天剩 3 条、今天 0 条 —— 那是日历翻页，不是你干完了）",
                !window.NoteWorkFinishedForProbe(DateTime.Today, entries: 0, open: 0) &&
                !window.IsAnimationPlaying,
                $"在播={window.CurrentAnimation ?? "（无）"}");

            window.ResetWorkFinishedBaselineForProbe();

            check("启动后第一次刷新不演（开机时本来就 0 条未收尾，不该蹦）",
                !window.NoteWorkFinishedForProbe(DateTime.Today, entries: 5, open: 0) &&
                !window.IsAnimationPlaying,
                $"在播={window.CurrentAnimation ?? "（无）"}");

            window.ResetWorkFinishedBaselineForProbe();
            window.NoteWorkFinishedForProbe(DateTime.Today, entries: 2, open: 2);

            check("把记录全删了不算完工（不是「做完了」，是「删了」）",
                !window.NoteWorkFinishedForProbe(DateTime.Today, entries: 0, open: 0) &&
                !window.IsAnimationPlaying,
                $"在播={window.CurrentAnimation ?? "（无）"}");

            // ---- 空白天到点下班照演 ----
            // 下班是"仪式感"，不是"今天干得不错"的评分，所以刻意不看当天的数据。
            day.Entries.Clear();
            window.RefreshStatusForProbe();

            check("一条记录都没有时，到点下班照演雀跃庆祝",
                window.PlayForReminderForProbe(ReminderKind.OffWork) &&
                window.CurrentAnimation == cheerAction,
                $"当天 {day.Entries.Count} 条记录，此刻在播 {window.CurrentAnimation ?? "（无）"}");

            player.AdvanceBy(TimeSpan.FromMilliseconds(500));

            check("雀跃庆祝演完就收手",
                !window.IsAnimationPlaying,
                $"在播={window.CurrentAnimation ?? "（无）"}");

            // ---- 别的提醒不该庆祝 ----
            // 下班前那条是"催你交活儿"，过了点还在催是"你还有事没做完" ——
            // 对着催办通知跳一段庆祝，是这套互动里最容易闹笑话的失效方式。
            check("下班前提醒不庆祝（那是催活儿，不是庆祝）",
                !window.PlayForReminderForProbe(ReminderKind.BeforeOffWork) &&
                !window.IsAnimationPlaying,
                $"在播={window.CurrentAnimation ?? "（无）"}");

            check("过了点还在催也不庆祝",
                !window.PlayForReminderForProbe(ReminderKind.OvertimeNudge) &&
                !window.IsAnimationPlaying,
                $"在播={window.CurrentAnimation ?? "（无）"}");

            sb.AppendLine($"  素材：1 个 archive + 1 个 cheer + 1 个点击动作（{Path.GetFileName(assetDir)}）");
        }
        catch (Exception ex)
        {
            check("归档与完工的互动", false, ex.Message);
            sb.AppendLine("      " + ex);
        }
        finally
        {
            // 数据一定要还原：这份 store 是内存里的真身，探针留下的条目会被后面的
            // 自检当成真数据看，还可能被下一次 Save 写进用户的书里。
            try
            {
                if (day != null)
                {
                    day.Entries = originalEntries ?? new List<NoteEntry>();
                    if (!dayWasThere) App.Store.Book.Days.Remove(day);
                }
            }
            catch { /* 还原不了也只能继续 */ }

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
    /// 闲置小动作按工作状态加权（见 <see cref="AmbientPicker"/>）。
    ///
    /// 这一节盯的是一件**安静失效**的事：hint 是动作名的子串，素材一改名就匹配不上，
    /// 而表现只是"加权不生效"（悄悄退回纯随机）—— 不报错、界面上完全看不出来。
    /// 所以前半段拿**真实的**闲置动作清单跟每条 hint 对账：零命中 = 改名了，
    /// 一次命中一大堆 = hint 写太短、把不相关的动作也捞进来了。
    ///
    /// 后半段造一份最小素材跑界面路径（扫描 → 按状态挑 → 真的播起来）。
    /// 挑的算法本身在纯逻辑自测里测 —— 那边能喂假数据、能跑几千次统计分布；
    /// 这里只验"接线接对了"，以及"加权不是硬映射"。
    /// </summary>
    private static void ProbeAmbientMood(CheckFn check, StringBuilder sb)
    {
        // ---------------- 前半：hint 跟真实素材对账 ----------------
        if (!CharacterPacks.IsAvailable(CharacterPacks.DshId))
        {
            sb.AppendLine("  （没装新版形象包，跳过闲置动作的加权断言）");
            return;
        }

        var dshRoot = CharacterPacks.DirOf(CharacterPacks.DshId);
        var realAmbient = AnimLibrary.Scan(dshRoot).ActionsWithRole(AnimActions.Ambient);
        if (realAmbient.Count == 0)
        {
            sb.AppendLine($"  （{dshRoot} 下没有 ambient-* 动作，跳过）");
            return;
        }

        var dead = new List<string>();     // 一条动作都命中不了 —— 素材改名了
        var greedy = new List<string>();   // 一捞一大把 —— hint 写太短

        foreach (var hint in AmbientPicker.AllHints)
        {
            var n = realAmbient.Count(a => a.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (n == 0) dead.Add(hint);
            else if (n > 3) greedy.Add($"{hint}→{n} 个");
        }

        check("每条 hint 都能在真实素材里找到对应动作（素材改名会让加权静默失效）",
            dead.Count == 0,
            dead.Count == 0
                ? $"{AmbientPicker.AllHints.Count} 条 hint 全部命中（对着 {realAmbient.Count} 个闲置动作）"
                : $"零命中 {dead.Count} 条：{string.Join("、", dead.Take(6))}");

        check("没有 hint 一次捞进太多动作（子串写太短会顺手带走无关动作）",
            greedy.Count == 0,
            greedy.Count == 0 ? "单条 hint 最多命中 3 个" : string.Join("、", greedy));

        // 典型场景各跑一遍，把"抬了几个"记进报告 —— 出问题时这一行能直接看出是哪个状态没配上
        foreach (var (label, ctx) in new[]
        {
            ("空白下午", new AmbientPicker.Context(new DateTime(2026, 1, 1, 15, 0, 0), 0, 0, 0, 18, null)),
            ("忙着推进", new AmbientPicker.Context(new DateTime(2026, 1, 1, 15, 0, 0), 4, 1, 0, 18, null)),
            ("堆着没收尾", new AmbientPicker.Context(new DateTime(2026, 1, 1, 15, 0, 0), 6, 3, 0, 18, null)),
            ("凌晨两点", new AmbientPicker.Context(new DateTime(2026, 1, 1, 2, 0, 0), 0, 0, 0, 18, null)),
        })
        {
            var w = FloatingWindow.WeighForProbe(
                realAmbient, ctx.Now, ctx.Entries, ctx.OpenEntries, ctx.Files, ctx.WorkEndHour, ctx.LastActivity);
            var hot = w.Count(kv => kv.Value > 1.0);
            sb.AppendLine($"  {label}：抬权 {hot}/{realAmbient.Count} 个　{AmbientPicker.Explain(ctx)}");

            // 全抬等于没加权，一个都不抬等于功能没生效 —— 两头都要拦
            check($"{label}下抬权的动作是少数但不止一个（全抬 = 没加权，零抬 = 没生效）",
                hot > 0 && hot <= realAmbient.Count / 2, $"抬权 {hot}/{realAmbient.Count}");
        }

        // ---------------- 后半：界面路径真的走一遍 ----------------
        var probeRoot = Path.Combine(AppPaths.LogDir, "ambient-mood-probe");
        var assetDir = Path.Combine(probeRoot, "Assets");

        const int frameSize = 720;
        var moodAction = AnimActions.Ambient + "-工作状态-忙碌点按";  // 被 InProgress/Backlog 抬权
        var plainAction = AnimActions.Ambient + "-探针毫不相干的动作"; // 不被任何 hint 命中

        string? previousAssetDir = null;
        FloatingWindow? window = null;
        var settings = App.Settings.Settings;

        try
        {
            previousAssetDir = Environment.GetEnvironmentVariable("WHALELOG_ASSET_DIR");

            // 位置挪到屏幕外、关掉自动收起（收起会顺手把坐标写进设置，外面那层快照兜底）
            settings.FloatingAnimations = true;
            settings.FloatingAutoCollapse = false;
            settings.FloatingLeft = Offscreen;
            settings.FloatingTop = Offscreen;

            WriteProbeAnimation(assetDir, moodAction, frameSize, 2, 2);
            WriteProbeAnimation(assetDir, plainAction, frameSize, 2, 2);

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

            check("两个探针动作都被认成闲置小动作",
                window.AmbientActionCount == 2,
                $"认到 {window.AmbientActionCount} 个");

            check("能说清此刻是什么工作状态（这一行就是「为什么今天总在叹气」的答案）",
                window.AmbientExplain.StartsWith("状态 [", StringComparison.Ordinal),
                window.AmbientExplain);

            var player = window.AnimPlayer;
            if (player == null)
            {
                check("播放器已建立", false, "拿不到播放器");
                return;
            }

            check("按状态挑出来的动作真的播起来了，而且记进了「最近播过」",
                window.PlayAmbientForProbe() &&
                window.AmbientRecent.Count == 1 &&
                window.AmbientRecent[0] == window.CurrentAnimation,
                $"在播 {window.CurrentAnimation ?? "（无）"}；最近队列 [{string.Join("、", window.AmbientRecent)}]");

            // 连挑若干次，两个动作都该露过面 —— 加权**不是**"只播这几个"的硬映射：
            // 没被抬权的动作权重仍是 1，永远有机会冒出来（小鲸鱼该有自己的想法）
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 24; i++)
            {
                player.AdvanceBy(TimeSpan.FromMilliseconds(500));   // 先确保上一段播完
                if (window.PlayAmbientForProbe() && window.CurrentAnimation is { } cur) seen.Add(cur);
            }

            check("连挑 24 次两个动作都露过面（加权不是硬映射，池子里所有动作都还在）",
                seen.Count == 2,
                $"出现过 {seen.Count} 个：{string.Join("、", seen)}");

            sb.AppendLine($"  探针素材：2 个闲置动作（{Path.GetFileName(assetDir)}）");
        }
        catch (Exception ex)
        {
            check("闲置动作按状态挑选", false, ex.Message);
            sb.AppendLine("      " + ex);
        }
        finally
        {
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

        // ⚠️ 画布刻意做成**矩形**（16:9），不是方形。
        // 方形是 2026-09-21 之前的硬假设：帧缓冲、行距、位图全按单边算，
        // 于是 16:9 的素材只能先 crop 成方的，道具被切成平口。
        // 探针走矩形这一条，才测得到"宽高分开算"这段新代码。
        const int cellW = 384;
        const int cellH = 216;
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

            // 源素材只有 192×192，靠帧表里的滤镜放成 384×216 的画布 ——
            // 顺便验证「运行时真的按帧表的滤镜解码」（滤镜写错/没生效时画布尺寸会不对）
            foreach (var action in probeActions)
                WriteProbeVideoAction(assetDir, action, clip, cellW, cellH);

            // 静态本体也得是同一块画布，否则静止↔播放会看到角色跳一下
            WriteProbeSolid(Path.Combine(assetDir, "whale.png"), cellW, cellH, 0, 0, 255);

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
                $"实际 {window.WhaleHeight:0.#}px（原版静态形象是 167.8px）");

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

            // 宽高分开断言：贴图那一侧最容易犯的错就是"行距按单边算"，
            // 640 宽的帧被按 360 步长读 —— 画面斜着撕开，而字节数一个不差、不报任何错。
            check("解出来的帧尺寸和帧表的画布一致（滤镜真按帧表生效了）",
                frameBitmap != null && frameBitmap.PixelWidth == cellW && frameBitmap.PixelHeight == cellH,
                frameBitmap == null
                    ? "没拿到帧位图"
                    : $"{frameBitmap.PixelWidth}×{frameBitmap.PixelHeight}，期望 {cellW}×{cellH}");

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
    /// 透明 webm 解出来的帧，**内容**到底对不对：alpha 在不在、红蓝有没有反。
    ///
    /// 这一节是补两次账攒出来的：1.4.1 之前形象动画一直顶着黑底显示；1.4.2 之前
    /// 颜色一直是红蓝互换的（深蓝紫头发显示成栗棕红）。两次都是同一个性质 ——
    /// ffmpeg 那个子进程照样一帧帧吐字节、帧数全对、不报任何错，
    /// 而当时的自检只断言了"帧数对不对""每帧字节数对不对"，**这两条它全过**。
    ///
    /// 所以这里不看帧数，直接看像素：
    ///   · alpha —— 现造一段左半不透明、右半全透明的 VP9 素材，数透明值；
    ///   · 通道序 —— 同一段素材的左半是**纯红**，红值必须落在 R 通道上。
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

            using var stream = new VideoFrameStream(ffmpeg, clip, filter: "",
                frameWidth: side, frameHeight: side, loop: false);

            check("透明素材点名了 libvpx 解码器", stream.UsingLibvpx,
                stream.UsingLibvpx ? "已点名" : "退回了 native —— 透明会丢");

            check("素材被判定为「带透明」", stream.HasAlpha);

            if (!stream.TryTake(out var frame, 10000) || frame == null)
            {
                check("解出一帧来数像素", false, "等了 10 秒没取到帧：" + stream.Error);
                return;
            }

            int clear = 0, opaque = 0;
            long sumB = 0, sumG = 0, sumR = 0;
            int sampled = 0;

            for (int i = 0; i + 3 < frame.Length; i += 4)
            {
                int a = frame[i + VideoFrameStream.OffsetA];
                if (a == 0) { clear++; continue; }
                if (a != 255) continue;
                opaque++;

                // 只采左半边靠中间的一条：色度是二次采样的，紧贴边界的像素会被糊到
                int px = (i / 4) % side;
                if (px < 6 || px >= side / 2 - 6) continue;

                sumB += frame[i + VideoFrameStream.OffsetB];
                sumG += frame[i + VideoFrameStream.OffsetG];
                sumR += frame[i + VideoFrameStream.OffsetR];
                sampled++;
            }

            int total = frame.Length / 4;

            // 这是整节的重点之一：native vp9 解出来的帧在这一条上会是 0 个透明像素
            check("解出来的帧真有透明像素（alpha 没被丢掉）",
                clear > 0,
                $"全透明 {clear}/{total} 个像素" +
                (clear == 0 ? " —— alpha 整条丢了，角色会带着黑底显示" : ""));

            check("解出来的帧也有实心像素（没把角色一起弄透明）",
                opaque > 0, $"全不透明 {opaque}/{total} 个像素");

            // 通道顺序：素材左半边是**纯红**，所以红值必须落在 R 通道上。
            //
            // 这一条同样是补的账：解码器以前吐的是 rgba，而贴图那边按 Bgra32 解读，
            // 红蓝整个对调 —— 头发从深蓝紫变成栗棕红（用户的话是"像反色了"），
            // 而上面那些"有没有透明像素""有没有实心像素"的断言**全部通过**。
            // 所以这里不看有没有值，只看值落在哪条通道。
            if (sampled > 0)
            {
                int avgB = (int)(sumB / sampled);
                int avgG = (int)(sumG / sampled);
                int avgR = (int)(sumR / sampled);

                check("红蓝通道没有互换（纯红素材解出来还是红的）",
                    avgR > avgB + 60 && avgR > avgG + 60,
                    $"左半实心像素均色 R={avgR} G={avgG} B={avgB}（偏移 " +
                    $"R={VideoFrameStream.OffsetR} B={VideoFrameStream.OffsetB}）" +
                    (avgR <= avgB + 60 ? " —— 红蓝反了，角色颜色会整个错" : ""));
            }
            else
            {
                check("左半采得到实心像素来验通道顺序", false, "一个像素都没采到");
            }
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
    private static void WriteProbeVideoAction(string assetDir, string action, string clipFile,
                                              int cellW, int cellH)
    {
        var dir = Path.Combine(assetDir, AnimLibrary.FolderName, action);
        Directory.CreateDirectory(dir);

        File.Copy(clipFile, Path.Combine(dir, "video.webm"), overwrite: true);

        // 角色高按"占画布高的 600/720"给 —— 和规范画布同一个比例，
        // 于是矩形画布算出来的**框高**和方形的样例一样是 198（框宽才是 352）。
        // 高度断言因此不用改，而宽度那一条正好验"两轴各算各的"。
        var charH = cellH * 600 / 720;

        File.WriteAllText(Path.Combine(dir, "anim.json"), $$"""
        {
          "name": "{{action}}", "version": 1, "type": "video",
          "video": "video.webm",
          "filter": "scale={{cellW}}:{{cellH}}",
          "frameWidth": {{cellW}}, "frameHeight": {{cellH}},
          "cols": 1, "rows": 1, "count": 24, "fps": 24,
          "loop": false,
          "anchor": { "x": {{cellW / 2}}, "y": {{cellH}} },
          "characterHeight": {{charH}},
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

            // 布局几何：格子有没有被容器裁掉。
            //
            // 来历是 2026-09-24 用户报的"当天日期的圆角是残缺的"。根因不在圆角，
            // 而在宽度算术：格子要 36 + 左右 Margin 各 1 = 38 DIP，七列 266；
            // 而面板当时宽 288，扣掉卡片 Margin(20)/Border(2)/Padding(24) 只剩 242 内容区，
            // 列宽 242/7 = 34.57 —— 装不下。WPF 在 Arrange 时发现子元素超出了分到的槽位，
            // 会给它套一个**矩形 layout clip**，右侧 3.43 DIP 连圆角一起切平成直角。
            //
            // 这种缺陷不报错、不违反任何数据断言，而且只有选中/悬停那种有实心背景的格子
            // 才看得出来，所以必须单独量。
            var laid = new MonthCalendarPanel();
            laid.Show(month, new DateTime(2026, 9, 11), marked);
            laid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            laid.Arrange(new Rect(new Point(0, 0), laid.DesiredSize));
            laid.UpdateLayout();

            if (laid.FindName("DayGrid") is UniformGrid dayGrid && dayGrid.Children.Count == 42)
            {
                double colWidth = dayGrid.ActualWidth / dayGrid.Columns;
                double side = (dayGrid.Children[0] as FrameworkElement)?.ActualWidth ?? 0;
                int clipped = dayGrid.Children.OfType<FrameworkElement>()
                    .Count(el => System.Windows.Media.VisualTreeHelper.GetClip(el) != null);

                check("日期格没被布局裁掉（列宽装得下 格宽 + 2×1 Margin）",
                    clipped == 0 && colWidth >= side + 2,
                    clipped == 0
                        ? $"列宽 {colWidth:F2} ≥ 格宽 {side:F1}+2"
                        : $"{clipped}/42 格被裁：列宽只有 {colWidth:F2}，需要 {side + 2:F1}");
            }
            else
            {
                check("日期格没被布局裁掉（列宽装得下 格宽 + 2×1 Margin）", false,
                    "找不到 DayGrid 或格数不是 42，量不了");
            }

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
