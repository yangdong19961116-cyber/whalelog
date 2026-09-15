using System.Text;
using WhaleLog.Models;
using WhaleLog.Services;

namespace WhaleLog.SelfTest;

/// <summary>
/// 文件归档链路的冒烟测试。
///
/// 重点验三件事：
///   1. 分类规则按预期命中（含优先级：精确规则要压过类型兜底规则）
///   2. 版本链判定正确（新文件 / 新版本 / 内容没变 / 重复，四种情况都不误判）
///   3. **原文件一个字节都不动** —— 这是整个工具最不能出错的地方
///
/// 运行：dotnet tools/selftest/bin/Release/net8.0/WhaleLog.SelfTest.dll
/// </summary>
internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    private static async Task<int> Main(string[] args)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "whalelog-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        var dataDir = Path.Combine(workspace, "data");
        var workDir = Path.Combine(workspace, "work");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(dataDir);

        // AppPaths 是惰性求值的，必须先设环境变量再第一次访问它
        Environment.SetEnvironmentVariable("WHALELOG_DATA_DIR", dataDir);

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"临时工作目录：{workspace}");
        Console.WriteLine($"数据目录：{AppPaths.Root}");
        Console.WriteLine();

        try
        {
            TestClassification();
            TestVersionNaming();
            TestReports();
            TestFileDateGrouping();
            TestSettingsClamp();
            TestAssetCatalog(workspace);
            TestCommandLine();
            TestAnimAssets(workspace);
            await TestEndToEndIntakeAsync(workDir);
            await TestDateScopedIntakeAsync(workspace);
            TestLargeFileSampling(dataDir);
            await TestSnapshotModeAsync(workspace);
        }
        catch (Exception ex)
        {
            Failures.Add("测试过程抛出异常：" + ex);
        }

        Console.WriteLine();
        Console.WriteLine(new string('-', 58));
        Console.WriteLine($"通过 {_passed} 项，失败 {Failures.Count} 项");
        foreach (var f in Failures)
        {
            Console.WriteLine("  ✗ " + f);
        }

        if (args.Contains("--keep"))
            Console.WriteLine($"已保留临时目录：{workspace}");
        else
            TryDelete(workspace);

        return Failures.Count == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------

    private static void TestClassification()
    {
        Section("分类规则");

        var classify = new ClassifyService();
        classify.Load();
        classify.ResetToDefaults();

        // 名字命中要压过类型兜底：周报既是"文档"也是"汇报材料"，应该归汇报材料
        CheckCategory(classify, "2026-W37 周报.docx", "汇报材料");
        CheckCategory(classify, "劳动合同.pdf", "合同协议");
        CheckCategory(classify, "项目评审纪要.docx", "会议材料");
        CheckCategory(classify, "需求方案 v2.docx", "需求方案");
        CheckCategory(classify, "预算表.xlsx", "表格数据");
        CheckCategory(classify, "季度汇报.pptx", "汇报材料");
        CheckCategory(classify, "设计稿.psd", "设计稿");
        CheckCategory(classify, "main.py", "代码");
        CheckCategory(classify, "照片.png", "图片");
        CheckCategory(classify, "资料.zip", "压缩包");
        CheckCategory(classify, "接口文档.pdf", "文档");
        CheckCategory(classify, "莫名其妙的文件.xyz", "未分类");

        // 命中规则名要能对上，方便用户回看"为什么归到这一类"
        var hit = classify.Classify("2026-W37 周报.docx", @"D:\工作\2026-W37 周报.docx");
        Check(hit.MatchedRule == "汇报与总结", $"周报应命中「汇报与总结」规则，实际：{hit.MatchedRule}");
    }

    private static void CheckCategory(ClassifyService classify, string fileName, string expected)
    {
        var hit = classify.Classify(fileName, Path.Combine(@"D:\工作", fileName));
        Check(hit.Category == expected, $"{fileName} 应归入「{expected}」，实际「{hit.Category}」");
    }

    private static void TestVersionNaming()
    {
        Section("版本链基名归一化");

        CheckEq(VersionService.NormalizeBaseName("报告_v2.docx"), "报告", "剥 v2 后缀");
        CheckEq(VersionService.NormalizeBaseName("报告(3).docx"), "报告", "剥括号序号");
        CheckEq(VersionService.NormalizeBaseName("报告 - 副本.docx"), "报告", "剥「副本」");
        CheckEq(VersionService.NormalizeBaseName("报告 第2版.docx"), "报告", "剥「第N版」");
        CheckEq(VersionService.NormalizeBaseName("报告_final.docx"), "报告", "剥 final");
        CheckEq(VersionService.NormalizeBaseName("报告 v2 (1).docx"), "报告", "叠加后缀一次剥干净");
        CheckEq(VersionService.NormalizeBaseName("报告.docx"), "报告", "不带后缀时原样保留");

        CheckEq(
            VersionService.BuildVersionGroupKey("报告_v2.docx"),
            VersionService.BuildVersionGroupKey("报告.docx"),
            "同名的不同版本应落在同一条链上");

        Check(
            VersionService.BuildVersionGroupKey("报告.docx") != VersionService.BuildVersionGroupKey("报告.pdf"),
            "docx 和 pdf 不应该被串成同一条版本链");
    }

    // ------------------------------------------------------------------

    private static void TestReports()
    {
        Section("日报与周报");

        // 一周起点换算（周一是起点）
        var monday = new DateTime(2026, 9, 7);
        for (int i = 0; i < 7; i++)
        {
            CheckEq(ReportService.WeekStart(monday.AddDays(i)).ToString("yyyy-MM-dd"),
                "2026-09-07", $"{ReportService.WeekdayText(monday.AddDays(i))}归属本周起点");
        }
        CheckEq(ReportService.WeekdayText(monday), "周一", "周一名称");
        CheckEq(ReportService.WeekdayText(monday.AddDays(5)), "周六", "周六名称");

        // 搭一天有内容的记录
        var day = monday.AddDays(2);          // 周三
        var book = new LogBook();
        var note = book.GetOrCreateDay(day);
        note.Summary = "打通了归档链路的版本判定。";
        note.Entries.Add(new NoteEntry
        {
            Text = "梳理归档流程", Category = NoteCategories.Work,
            CreatedAt = day.AddHours(9).AddMinutes(20), Done = true,
            DoneAt = day.AddHours(11).AddMinutes(5),
        });
        note.Entries.Add(new NoteEntry
        {
            Text = "和设计对齐收起交互", Category = NoteCategories.Comm, Tag = "悬浮窗",
            CreatedAt = day.AddHours(11).AddMinutes(30), Done = true,
            DoneAt = day.AddHours(12),
        });
        note.Entries.Add(new NoteEntry
        {
            Text = "补采样指纹的边界用例", Category = NoteCategories.Work,
            CreatedAt = day.AddHours(14).AddMinutes(30),
        });
        book.Files.Add(new FileRecord
        {
            FileName = "2026-W37 周报.xlsx", FullPath = @"D:\工作\2026-W37 周报.xlsx",
            Category = "汇报材料", AddedDate = day.ToString("yyyy-MM-dd"),
            Version = 1, SizeBytes = 18432, LastWriteTime = day.AddHours(11).AddMinutes(49),
        });
        book.Files.Add(new FileRecord
        {
            FileName = "报告.docx", FullPath = @"D:\工作\报告.docx",
            Category = "汇报材料", AddedDate = day.ToString("yyyy-MM-dd"),
            Version = 3, SizeBytes = 1468006, LastWriteTime = day.AddHours(12),
        });
        book.Files.Add(new FileRecord
        {
            FileName = "intake_service.py", FullPath = @"D:\repo\intake_service.py",
            Category = "代码", AddedDate = day.ToString("yyyy-MM-dd"),
            Version = 1, SizeBytes = 12288, LastWriteTime = day.AddHours(10).AddMinutes(31),
        });

        var entriesBefore = note.Entries.Count;

        var daily = ReportService.BuildDaily(book, day);
        Check(daily.Contains("工作日报"), "日报带标题");
        Check(daily.Contains("周三"), "日报带星期");
        Check(daily.Contains("记录 **3** 条"), "日报统计条目数");
        Check(daily.Contains("今日完成"), "日报有「今日完成」小节");
        Check(daily.Contains("进行中"), "日报有「进行中」小节");
        Check(daily.Contains("梳理归档流程"), "日报含事项正文");
        Check(daily.Contains("和设计对齐收起交互"), "日报含事项正文（第二条）");
        Check(daily.Contains("悬浮窗"), "日报含自定义标签");
        Check(daily.Contains("补采样指纹的边界用例"), "未完成事项也进日报");
        Check(daily.Contains("汇报材料"), "日报按类别汇总文件");
        Check(daily.Contains("报告.docx（v3）"), "日报标出文件版本");
        Check(daily.Contains("intake_service.py"), "日报含代码类文件");
        Check(daily.Contains("打通了归档链路的版本判定"), "日报引用手写小结");

        Check(note.Entries.Count == entriesBefore, "生成日报不应改动原始记录");

        var emptyDaily = ReportService.BuildDaily(book, day.AddDays(-1));
        Check(emptyDaily.Contains("没有任何记录"), "空白日期的日报要明说没有记录");

        var weekly = ReportService.BuildWeekly(book, day);
        Check(weekly.Contains("2026-09-07 ~ 2026-09-13"), "周报标题是周一到周日");
        Check(weekly.Contains("分日明细"), "周报有分日明细");
        Check(weekly.Contains("涉及文件"), "周报有文件汇总");
        Check(weekly.Contains("报告.docx（v3）"), "周报标出文件版本");
        Check(weekly.Contains("概览"), "周报有概览");

        // 落盘
        Check(ReportService.TryWriteDaily(book, day, out var dailyPath, out var msg),
            $"日报写盘：{msg}");
        Check(File.Exists(dailyPath) && File.ReadAllText(dailyPath).Contains("工作日报"),
            "日报文件内容正确");

        Check(ReportService.TryWriteWeekly(book, day, out var weeklyPath, out var wmsg),
            $"周报写盘：{wmsg}");
        Check(File.Exists(weeklyPath), "周报文件已生成");
    }

    /// <summary>
    /// 归档的归属日期。
    ///
    /// 拖入的文件归"当前正在查看的那一天"（forDate），不是字面的今天 —— 这样才能补录过去某天的文件。
    /// 界面上的「只看当天」用的就是这条规则：一旦归属日期和筛选口径对不上，
    /// 用户就会看到"文件拖进去了，却在今天找不到"。
    /// </summary>
    private static async Task TestDateScopedIntakeAsync(string workspace)
    {
        Section("归档归属日期");

        var workDir = Path.Combine(workspace, "dated");
        Directory.CreateDirectory(workDir);

        var past = DateTime.Today.AddDays(-3);
        var file = Path.Combine(workDir, "补录材料.txt");
        File.WriteAllText(file, "这是补录到过去某一天的文件");

        var settings = new SettingsService();
        settings.Load();
        settings.Update(s => s.SnapshotMode = false);

        var store = new LogBookStore();
        store.Load();
        var classify = new ClassifyService();
        classify.Load();
        var intake = new FileIntakeService(store, classify, new VersionService(settings), settings);

        var result = await intake.IntakeAsync(new[] { file }, past);
        Check(result.Count == 1 && result[0].Status == IntakeStatus.Added,
            $"指定日期归档应判为新增，实际 {(result.Count == 1 ? result[0].Status.ToString() : $"{result.Count} 条结果")}");

        var rec = store.FindFileByPath(file);
        Check(rec != null && rec.AddedDate == past.ToString("yyyy-MM-dd"),
            $"记录应归属 {past:yyyy-MM-dd}，实际 {rec?.AddedDate ?? "（找不到记录）"}");

        Check(store.FilesOnDate(past).Any(f => f.FullPath == file),
            $"切到 {past:M月d日} 用「只看当天」应该看得到它");
        Check(!store.FilesOnDate(DateTime.Today).Any(f => f.FullPath == file),
            "用「只看当天」看今天时不应该看到它（这正是用户会困惑的地方，所以横幅必须写明目标日期）");
        Check(store.QueryFiles(null).Any(f => f.FullPath == file),
            "切到「全部」应该能看到它");

        // 默认（不传 forDate）落在今天
        var todayFile = Path.Combine(workDir, "今天的材料.txt");
        File.WriteAllText(todayFile, "今天拖进来的");
        await intake.IntakeAsync(new[] { todayFile });
        Check(store.FindFileByPath(todayFile)?.AddedDate == DateTime.Today.ToString("yyyy-MM-dd"),
            "不指定日期时应默认归属今天");
    }

    /// <summary>
    /// 「全部」视图按归档日期分组的排序与栏头文案。
    ///
    /// 这类"顺序"错误在界面上几乎看不出来 —— 每一栏自己都长得正常，只是次序不对。
    /// 所以期望值全部写死，包括跨年和没有归属日期这些边角。
    /// </summary>
    /// <summary>
    /// 外部素材的定位逻辑。这一层是纯文件 IO、不碰 WPF，所以能直接断言。
    /// </summary>
    private static void TestAssetCatalog(string workspace)
    {
        var dir = Path.Combine(workspace, "assets");
        var missing = Path.Combine(workspace, "no-such-assets");

        // 目录不存在不能抛，要安静返回 null（那时走内置素材）
        Check(AssetCatalog.FindExternal("whale", missing) == null,
            "素材目录不存在时应返回 null");

        Check(AssetCatalog.FindExternal("not-a-real-asset", dir) == null,
            "未知素材名应返回 null");

        Directory.CreateDirectory(dir);

        // 只放了别名文件也要认得出来 —— 用户手里那张可能就叫 DSniang1.png
        var alias = Path.Combine(dir, "DSniang1.png");
        File.WriteAllBytes(alias, new byte[] { 1 });
        Check(SamePath(AssetCatalog.FindExternal("whale", dir), alias),
            "只存在别名文件时也应能定位到");

        // 主名和别名同时存在 → 主名优先（素材说明文件里写的是主名）
        var main = Path.Combine(dir, "whale.png");
        File.WriteAllBytes(main, new byte[] { 2 });
        Check(SamePath(AssetCatalog.FindExternal("whale", dir), main),
            "主名与别名同时存在时应优先主名");

        // 每个素材只认自己那组候选名，不能串到别人的文件上
        Check(AssetCatalog.FindExternal("whale-head", dir) == null,
            "别的素材的文件不应被当成这个素材");

        // 清单本身要自洽
        Check(AssetCatalog.All.Select(e => e.Name).Distinct().Count() == AssetCatalog.All.Count,
            "素材逻辑名不能重复");
        Check(AssetCatalog.All.All(e => e.Files.Length > 0 && !string.IsNullOrWhiteSpace(e.Files[0])),
            "每个素材至少要有主文件名");
        Check(AssetCatalog.All.All(e => !Path.IsPathRooted(e.Files[0]) && !e.Files[0].Contains('\\')),
            "候选文件名必须是纯文件名，不能带路径 —— 否则会写到素材目录之外");
        Check(AssetCatalog.All.All(e => e.EmbeddedUri.StartsWith("pack://", StringComparison.Ordinal)),
            "内置资源必须是 pack:// URI");
        Check(AssetCatalog.All.All(e => !string.IsNullOrWhiteSpace(e.Spec)),
            "每个素材都要有规格说明，它会被写进素材说明文件");
        Check(AssetCatalog.Find("whale") != null && AssetCatalog.Find("WHALE") != null,
            "素材名应当大小写不敏感");

        // 候选文件名跨素材不能重复：一个文件被两个素材同时认领，
        // 后果是"本体"和"悬浮态"变成同一张图 —— 切了等于没切，而且一声不响
        var allCandidates = AssetCatalog.All.SelectMany(e => e.Files).ToList();
        Check(allCandidates.Count == allCandidates.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "候选文件名不能跨素材重复，否则一个文件会被两个素材同时认领");

        // 悬浮态形象（表情差分）这个位子要在，且必须写清"和本体同画布"这条硬要求 ——
        // 画布不一致时鼠标一移上去角色就会跳一下，而这在界面上极难归因
        var hover = AssetCatalog.Find("whale-hover");
        Check(hover != null, "要有「鼠标悬浮时的形象」这个素材位");
        Check(hover != null && hover.Spec.Contains("画布", StringComparison.Ordinal),
            "悬浮形象的规格说明必须写明「和本体同画布」，否则画师无从知道要对齐");

        // Describe 的措辞是自检报告和界面提示的来源
        var withExternal = AssetCatalog.Describe("whale", dir);
        Check(withExternal.Contains("whale.png"),
            $"有外部文件时 Describe 应写出文件名，实际「{withExternal}」");

        var fallback = AssetCatalog.Describe("whale-head", dir);
        Check(fallback.Contains("内置"),
            $"没有外部文件时 Describe 应说明用内置，实际「{fallback}」");
    }

    private static bool SamePath(string? a, string? b)
        => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 命令行开关解析。踩过的坑：`--export-assets --quiet` 会把 "--quiet"
    /// 当成目录名，素材被导出到一个叫 `--quiet` 的目录里 ——
    /// 这类边界在界面上完全看不出来。
    /// </summary>
    private static void TestCommandLine()
    {
        var empty = new CommandLine(null);
        Check(!empty.Has("--tray"), "空参数里不该有开关");
        Check(empty.Value("--shot") == null, "空参数取值应为 null");

        var cmd = new CommandLine(new[]
        {
            "--tray", "--selfcheck", "--quiet",
            "--shot=D:\\tmp\\a.png",
            "--shot-notes", "D:\\tmp\\b.png",
        });

        Check(cmd.Has("--tray") && cmd.Has("--TRAY"), "开关名应当大小写不敏感");
        Check(cmd.Has("--shot"), "`--x=值` 也算有这个开关");
        Check(!cmd.Has("--shot-floating"), "不该把名字相近的开关认成同一个");

        CheckEq(cmd.Value("--shot"), @"D:\tmp\a.png", "`--x=值` 应能取值");
        CheckEq(cmd.Value("--shot-notes"), @"D:\tmp\b.png", "`--x 值` 应能取值");

        // 核心：后面紧跟另一个开关时，不能把它当成值
        Check(cmd.Value("--selfcheck") == null,
            "开关后面紧跟另一个开关时取值应为 null（曾把 --quiet 当成目录名）");
        Check(cmd.Value("--tray") == null, "没有值的开关取值应为 null");
        Check(cmd.Value("--nope") == null, "不存在的开关取值应为 null");

        // 用户真会这么敲：值后面还跟着别的开关
        var tail = new CommandLine(new[] { "--export-assets", "D:\\out", "--quiet" });
        CheckEq(tail.Value("--export-assets"), @"D:\out", "值后面跟开关时仍应取到值");

        var lonely = new CommandLine(new[] { "--shot" });
        Check(lonely.Value("--shot") == null, "开关在末尾时应取不到值");
    }

    private static void TestFileDateGrouping()
    {
        Section("按日期分组");

        var today = new DateTime(2026, 9, 11);   // 周五

        Check(DateGrouping.Label("2026-09-11", today) == "今天 · 9月11日",
            $"实际「{DateGrouping.Label("2026-09-11", today)}」");
        Check(DateGrouping.Label("2026-09-10", today) == "昨天 · 9月10日",
            $"实际「{DateGrouping.Label("2026-09-10", today)}」");
        Check(DateGrouping.Label("2026-09-07", today) == "周一 · 9月7日",
            $"更早的日期应显示星期，实际「{DateGrouping.Label("2026-09-07", today)}」");
        Check(DateGrouping.Label("2025-12-31", today) == "周三 · 2025年12月31日",
            $"跨年必须带年份，实际「{DateGrouping.Label("2025-12-31", today)}」");
        Check(DateGrouping.Label("", today) == "未标注日期",
            $"实际「{DateGrouping.Label("", today)}」");
        Check(DateGrouping.Label(null, today) == "未标注日期",
            "传 null 也不该抛异常");

        static FileRecord R(string name, string date, int hour) => new()
        {
            FileName = name,
            AddedDate = date,
            AddedAt = new DateTime(2026, 9, 11, hour, 0, 0),
        };

        var files = new List<FileRecord>
        {
            R("b.txt", "2026-09-07", 9),
            R("a.txt", "2026-09-11", 8),
            R("c.txt", "2026-09-07", 17),
            R("d.txt", "", 12),
        };

        var sorted = DateGrouping.Sort(files).Select(f => f.FileName).ToList();
        Check(sorted.SequenceEqual(new[] { "a.txt", "c.txt", "b.txt", "d.txt" }),
            $"期望 a → c → b → d（日期倒序、同日按拖入时刻倒序、无日期最后），实际 {string.Join(" → ", sorted)}");

        Check(DateGrouping.CountGroups(files) == 3,
            $"应算出 3 组（09-11 / 09-07 / 未标注），实际 {DateGrouping.CountGroups(files)}");

        // 行内时间戳必须和分组栏头同口径：用归属日期，不是拖入时刻所在的日期。
        // （真实数据里出现过 addedAt=9/11 而 addedDate=9/7 的记录）
        var odd = R("blend", "2026-09-07", 15);
        Check(odd.AddedStampText == "9月7日 15:00",
            $"行内应显示归属日期，实际「{odd.AddedStampText}」");
        Check(odd.AddedStampText.Contains(odd.AddedDateText, StringComparison.Ordinal),
            "行内时间戳必须包含它所属分组的日期，否则栏头和行里会不一致");

        Check(R("old", "", 9).AddedStampText == "09:00",
            $"没有归属日期时只显示时刻，实际「{R("old", "", 9).AddedStampText}」");
    }

    /// <summary>
    /// 设置文件的越界值必须被拉回合法区间。
    ///
    /// 界面滑杆的 Minimum/Maximum 和这里的校验共用 AppSettings 里同一组常量；
    /// 两边各写一份的话，手改过 settings.json 就会出现"滑杆显示一个数、实际用另一个数"。
    /// </summary>
    private static void TestSettingsClamp()
    {
        Section("设置校验");

        var path = AppPaths.SettingsFile;
        File.WriteAllText(path,
            """{"floatingScale": 99, "floatingOpacity": -5, "workEndHour": 77, "remindMinutesBefore": 99999}""");

        var settings = new SettingsService();
        settings.Load();

        Check(Math.Abs(settings.Settings.FloatingScale - AppSettings.MaxFloatingScale) < 1e-9,
            $"浮窗大小应被夹到 {AppSettings.MaxFloatingScale}，实际 {settings.Settings.FloatingScale}");
        Check(Math.Abs(settings.Settings.FloatingOpacity - AppSettings.MinFloatingOpacity) < 1e-9,
            $"不透明度应被夹到 {AppSettings.MinFloatingOpacity}，实际 {settings.Settings.FloatingOpacity}");
        Check(settings.Settings.WorkEndHour == 23, $"下班小时应被夹到 23，实际 {settings.Settings.WorkEndHour}");
        Check(settings.Settings.RemindMinutesBefore == 240,
            $"提前提醒分钟应被夹到 240，实际 {settings.Settings.RemindMinutesBefore}");

        // 范围本身要合理，否则夹完还是坏的
        Check(AppSettings.MinFloatingScale > 0 && AppSettings.MinFloatingScale < AppSettings.MaxFloatingScale,
            $"浮窗大小范围 {AppSettings.MinFloatingScale}–{AppSettings.MaxFloatingScale}");
        Check(AppSettings.MinFloatingOpacity > 0 && AppSettings.MinFloatingOpacity <= AppSettings.MaxFloatingOpacity,
            $"不透明度范围 {AppSettings.MinFloatingOpacity}–{AppSettings.MaxFloatingOpacity}");

        // 清掉，免得影响后续测试的默认值
        try { File.Delete(path); } catch { /* 删不掉就算了 */ }
        var fresh = new SettingsService();
        fresh.Load();
        Check(Math.Abs(fresh.Settings.FloatingScale - 1.0) < 1e-9,
            $"配置缺失时应回到默认值 1.0，实际 {fresh.Settings.FloatingScale}");

        // 全新安装时 FloatingLeft/Top 是 NaN（表示"还没保存过位置"），
        // 而 JSON 规范里没有 NaN 字面量 —— 不加 AllowNamedFloatingPointLiterals 的话
        // 序列化会抛异常。因为 Save 是容错的，表现就是"设置怎么改都存不下去"，
        // 而且完全没有痕迹。这条断言就是钉住它。
        var nanCase = new SettingsService();
        nanCase.Load();
        Check(double.IsNaN(nanCase.Settings.FloatingLeft),
            $"没保存过位置时 FloatingLeft 应为 NaN，实际 {nanCase.Settings.FloatingLeft}");

        nanCase.Update(x => x.ThemeOverride = AppThemeMode.Dark);
        Check(File.Exists(AppPaths.SettingsFile),
            "浮动窗位置还是 NaN 时，设置也必须能落盘（否则全新安装改什么都不生效）");

        var reloaded = new SettingsService();
        reloaded.Load();
        Check(reloaded.Settings.ThemeOverride == AppThemeMode.Dark,
            $"主题应能读回 Dark，实际 {reloaded.Settings.ThemeOverride}");
        Check(double.IsNaN(reloaded.Settings.FloatingLeft),
            "NaN 位置往返之后应该还是 NaN，而不是变成 0");
        Check(reloaded.Settings.FloatingTop is double v && double.IsNaN(v),
            "上下两个坐标都要能往返");
    }

    private static async Task TestEndToEndIntakeAsync(string workDir)    {
        Section("端到端归档");

        // 准备素材
        var report = Path.Combine(workDir, "报告.docx");
        var reportV2 = Path.Combine(workDir, "报告_v2.docx");
        var weekly = Path.Combine(workDir, "2026-W37 周报.xlsx");
        var code = Path.Combine(workDir, "main.py");
        var photo = Path.Combine(workDir, "照片.png");
        var unknown = Path.Combine(workDir, "东西.xyz");

        File.WriteAllText(report, "第一版正文：项目背景与目标");
        File.WriteAllText(reportV2, "第二版正文：补上了进度表和风险项");
        File.WriteAllText(weekly, "本周完成事项");
        File.WriteAllText(code, "print('hello')");
        File.WriteAllBytes(photo, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 });
        File.WriteAllText(unknown, "无法归类的文件");

        // 显式铺开修改时间：一次拖进整个文件夹时，归档顺序按修改时间升序，
        // 这样「报告.docx 拿 v1、报告_v2.docx 拿 v2」才是确定的结果而不是碰运气。
        var baseTime = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(report, baseTime.AddMinutes(-30));
        File.SetLastWriteTimeUtc(reportV2, baseTime.AddMinutes(-20));
        File.SetLastWriteTimeUtc(weekly, baseTime.AddMinutes(-15));
        File.SetLastWriteTimeUtc(code, baseTime.AddMinutes(-10));
        File.SetLastWriteTimeUtc(photo, baseTime.AddMinutes(-5));
        File.SetLastWriteTimeUtc(unknown, baseTime.AddMinutes(-1));

        // 记下改造前的状态，后面要逐一比对
        var snapshot = Snapshot(workDir);
        var reportHashBefore = VersionService.ComputeFullHash(report).Hash;

        var settings = new SettingsService();
        settings.Load();
        settings.Update(s => s.SnapshotMode = false);

        var store = new LogBookStore();
        store.Load();
        var classify = new ClassifyService();
        classify.Load();
        var version = new VersionService(settings);
        var intake = new FileIntakeService(store, classify, version, settings);

        // --- 第一轮：首次归档 ---
        var first = await intake.IntakeAsync(new[] { workDir });
        Check(first.Count == 6, $"应归档 6 个文件，实际 {first.Count}");
        Check(first.Count(r => r.Status == IntakeStatus.Added) == 5,
            "首次归档时，5 个互不相关的文件应判为新增");
        Check(first.Count(r => r.Status == IntakeStatus.NewVersion) == 1,
            "报告_v2.docx 应判为「版本链上的新版本」而不是全新文件");

        var reportRecord = store.FindFileByPath(report);
        Check(reportRecord != null, "报告.docx 应该出现在归档记录里");
        Check(reportRecord!.Version == 1, $"报告.docx 应该是 v1，实际 v{reportRecord.Version}");
        Check(reportRecord.Category == "汇报材料", $"报告.docx 应归入汇报材料，实际「{reportRecord.Category}」");
        Check(!reportRecord.IsMissing, "报告.docx 不应被标记为丢失");

        var v2Record = store.FindFileByPath(reportV2);
        Check(v2Record != null, "报告_v2.docx 应该出现在归档记录里");
        Check(v2Record!.Version == 2, $"报告_v2.docx 应挂到版本链的 v2，实际 v{v2Record.Version}");
        Check(
            reportRecord.VersionGroupKey == v2Record.VersionGroupKey,
            "报告.docx 和 报告_v2.docx 应共用同一条版本链");
        Check(v2Record.History.Count == 1, "每个版本都应该有一条历史记录");

        // 分类抽样
        Check(store.FindFileByPath(weekly)?.Category == "汇报材料", "周报应归入汇报材料");
        Check(store.FindFileByPath(code)?.Category == "代码", "main.py 应归入代码");
        Check(store.FindFileByPath(photo)?.Category == "图片", "照片应归入图片");
        Check(store.FindFileByPath(unknown)?.Category == "未分类", "未知扩展名应归入未分类");

        // --- 原文件必须纹丝不动 ---
        CheckSnapshotUnchanged(snapshot, workDir, "第一轮归档后");
        Check(
            VersionService.ComputeFullHash(report).Hash == reportHashBefore,
            "归档不应改变原文件内容");

        // --- 第二轮：原样再拖一次 ---
        var second = await intake.IntakeAsync(new[] { workDir });
        Check(second.All(r => r.Status == IntakeStatus.Duplicate),
            "同样的文件再拖一次，应全部判为重复，不产生新版本");
        Check(store.FindFileByPath(report)!.Version == 1, "重复归档不应把版本号顶上去");

        // --- 第三轮：同一内容换个文件名 ---
        var reportV3 = Path.Combine(workDir, "报告_v3.docx");
        File.WriteAllText(reportV3, "第一版正文：项目背景与目标");   // 和 v1 内容完全一致
        var third = await intake.IntakeAsync(new[] { reportV3 });
        Check(third.Count == 1 && third[0].Status == IntakeStatus.Unchanged,
            $"内容与 v1 一致时应判为「未产生新版本」，实际 {third[0].Status}");

        // --- 第四轮：真的改了内容 ---
        File.WriteAllText(report, "第三版正文：重写了背景，并补了里程碑");
        var fourth = await intake.IntakeAsync(new[] { report });
        Check(fourth.Count == 1 && fourth[0].Status == IntakeStatus.NewVersion,
            $"内容变了应判为新版本，实际 {fourth[0].Status}");
        Check(store.FindFileByPath(report)!.Version == 3,
            $"应升到 v3，实际 v{store.FindFileByPath(report)!.Version}");

        var chain = store.VersionChain(reportRecord.VersionGroupKey);
        Check(chain.Count == 2, $"版本链里应有 2 条记录（报告.docx / 报告_v2.docx），实际 {chain.Count}");
        Check(chain.Any(c => !string.IsNullOrEmpty(c.History.FirstOrDefault()?.Sha256)),
            "每条版本记录都应带指纹");

        // --- 落盘产物 ---
        var dayFile = Path.Combine(AppPaths.NotesDir, AppPaths.ToDayFileName(DateTime.Today));
        Check(File.Exists(AppPaths.IndexFile), $"应生成索引文件 {AppPaths.IndexFile}");
        Check(File.Exists(dayFile), $"应生成当天笔记 {dayFile}");

        if (File.Exists(dayFile))
        {
            var md = File.ReadAllText(dayFile);
            Check(md.Contains("report") || md.Contains("报告"), "笔记里应能读到归档文件的信息");
        }

        var json = File.ReadAllText(AppPaths.IndexFile);
        Check(json.Contains("报告_v2.docx"), "索引里应包含文件名");
        Check(json.Contains(workDir.Replace("\\", "\\\\")),
            "索引里应记录文件所在的完整位置（这是「记住文件在哪儿」的核心）");

        // --- 快照模式默认不落地 ---
        Check(!Directory.Exists(AppPaths.ArchiveDir) ||
              !Directory.EnumerateFileSystemEntries(AppPaths.ArchiveDir).Any(),
            "未开启快照模式时，archive 目录不应产生任何副本");

        // --- 文件被移走后要能标出来（但记录保留）---
        File.Delete(unknown);
        store.RefreshMissingFlags();
        Check(store.FindFileByPath(unknown)!.IsMissing, "原文件被删除后应标记为已不在原位置");
    }

    private static void TestLargeFileSampling(string dataDir)
    {
        Section("大文件采样指纹");

        var big = Path.Combine(dataDir, "bigfile.bin");
        var dir = Path.GetDirectoryName(big)!;
        Directory.CreateDirectory(dir);

        var payload = new byte[3 * 1024 * 1024];
        new Random(42).NextBytes(payload);
        File.WriteAllBytes(big, payload);

        var settings = new SettingsService();
        settings.Load();
        settings.Update(s => s.FullHashLimitMb = 1);   // 把阈值压到 1MB，逼它走采样分支
        var version = new VersionService(settings);

        var first = version.ComputeHash(big);
        Check(first.Sampled, "超过阈值的文件应使用采样指纹");
        Check(first.Hash.StartsWith("sample:"), "采样指纹应带 sample: 前缀，避免和全量指纹混淆");

        var second = version.ComputeHash(big);
        Check(first.Hash == second.Hash, "同一文件两次采样指纹应一致");

        // 改开头一定会被采到
        payload[10] ^= 0xFF;
        File.WriteAllBytes(big, payload);
        var changed = version.ComputeHash(big);
        Check(changed.Hash != first.Hash, "改动落在采样区间内时，指纹必须变化");

        // 长度变了也必须变化（长度参与指纹计算）
        var longer = new byte[payload.Length + 4096];
        Array.Copy(payload, longer, payload.Length);
        File.WriteAllBytes(big, longer);
        Check(version.ComputeHash(big).Hash != changed.Hash, "文件长度变化必须导致指纹变化");

        settings.Update(s => s.FullHashLimitMb = 200);
        File.Delete(big);
    }

    private static async Task TestSnapshotModeAsync(string workspace)
    {
        Section("快照模式");

        var workDir = Path.Combine(workspace, "snapshot-work");
        Directory.CreateDirectory(workDir);
        var file = Path.Combine(workDir, "快照测试.docx");
        File.WriteAllText(file, "需要留档的内容");

        var settings = new SettingsService();
        settings.Load();
        settings.Update(s => s.SnapshotMode = true);

        var store = new LogBookStore();
        store.Load();
        var classify = new ClassifyService();
        classify.Load();
        var version = new VersionService(settings);
        var intake = new FileIntakeService(store, classify, version, settings);

        var result = await intake.IntakeAsync(new[] { file });
        Check(result.Count == 1 && result[0].Status == IntakeStatus.Added, "快照模式下应正常归档");

        var record = store.FindFileByPath(file);
        Check(record != null, "快照模式下应有归档记录");
        Check(!string.IsNullOrEmpty(record!.SnapshotPath), "快照模式下应记录副本路径");
        Check(record.SnapshotPath != null && File.Exists(record.SnapshotPath), "快照副本应真实存在");
        Check(
            record.SnapshotPath != null && File.ReadAllText(record.SnapshotPath) == "需要留档的内容",
            "快照副本内容应与原文件一致");
        Check(File.Exists(file), "开启快照后原文件仍然在原地");

        settings.Update(s => s.SnapshotMode = false);
    }

    // ------------------------------------------------------------------
    // 断言与工具
    // ------------------------------------------------------------------

    /// <summary>记录目录下每个文件的路径、字节内容和最后写入时间。</summary>
    private static Dictionary<string, (byte[] Bytes, DateTime Written)> Snapshot(string root)
    {
        var map = new Dictionary<string, (byte[], DateTime)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            map[path] = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));
        return map;
    }

    private static void CheckSnapshotUnchanged(
        Dictionary<string, (byte[] Bytes, DateTime Written)> before, string root, string stage)
    {
        var after = Snapshot(root);

        Check(after.Count == before.Count,
            $"{stage}：文件数量发生了变化（{before.Count} → {after.Count}），原文件不该被增删");

        foreach (var (path, state) in before)
        {
            if (!after.TryGetValue(path, out var now))
            {
                Check(false, $"{stage}：文件 {Path.GetFileName(path)} 被移动或删除了");
                continue;
            }

            Check(now.Bytes.AsSpan().SequenceEqual(state.Bytes),
                $"{stage}：文件 {Path.GetFileName(path)} 的内容被改动了");
            Check(now.Written == state.Written,
                $"{stage}：文件 {Path.GetFileName(path)} 的修改时间被改动了");
        }
    }

    // ==================================================================
    // 动态素材（悬浮窗的精灵图动画）
    //
    // 播放器本身是 WPF 的，测不了；但"帧表怎么读、帧怎么切、目录怎么扫、
    // 显示框该多大"全是纯逻辑 —— 而这几件事恰恰是最容易悄悄错的地方：
    // 列宽算错一位，整个动画就整体错位；框高漏算，角色在屏幕上就小一圈。
    // ==================================================================

    /// <summary>仓库里那份样例帧表，原样抄进来当"规格的活样本"。</summary>
    private const string SampleSheetJson = """
    {
      "name": "clickBounce", "version": 1, "type": "sheet",
      "sheet": "sheet.png",
      "frameWidth": 720, "frameHeight": 720,
      "cols": 4, "rows": 2, "count": 8,
      "fps": 24, "loop": false,
      "anchor": { "x": 360, "y": 660 },
      "characterHeight": 600, "returnTo": "base", "sound": ""
    }
    """;

    private static void TestAnimAssets(string workspace)
    {
        Section("动态素材：帧表解析");

        var parsed = AnimSheet.Parse(SampleSheetJson);
        Check(parsed.Ok, "样例帧表能解析" + (parsed.Ok ? "" : "：" + parsed.Error));
        if (parsed.Sheet is not { } sheet) return;

        CheckEq(sheet.Count.ToString(), "8", "帧数");
        CheckEq(sheet.Fps.ToString("0.##"), "24", "帧率");
        CheckEq(sheet.FrameDuration.TotalMilliseconds.ToString("0.###"), "41.667", "单帧时长（ms）");
        Check(sheet.HasCharacterHeight, "characterHeight 被认出来了");
        Check(!sheet.Loop, "loop=false 认成" + (sheet.Loop ? "循环" : "播一次"));

        // ------------------------------------------------------------------
        Section("动态素材：切帧（行优先）");

        (int X, int Y, int W, int H) Rect(int i)
            => sheet.TryFrameRect(i, out var x, out var y, out var w, out var h)
                ? (x, y, w, h) : (-1, -1, -1, -1);

        CheckEq(Rect(0).ToString(), "(0, 0, 720, 720)", "第 0 帧在左上角");
        CheckEq(Rect(3).ToString(), "(2160, 0, 720, 720)", "第 3 帧在同一行最右");
        CheckEq(Rect(4).ToString(), "(0, 720, 720, 720)", "第 4 帧换到第二行左边");
        CheckEq(Rect(7).ToString(), "(2160, 720, 720, 720)", "第 7 帧在右下角");
        Check(!sheet.TryFrameRect(8, out _, out _, out _, out _), "越界的帧号被拒绝（不抛异常）");
        Check(!sheet.TryFrameRect(-1, out _, out _, out _, out _), "负帧号被拒绝");

        var (ew, eh) = sheet.ExpectedSheetSize;
        CheckEq($"{ew}×{eh}", "2880×1440", "按帧表算出的精灵图应有尺寸");

        // ------------------------------------------------------------------
        // 最容易出错、后果最明显的一条：画布 610→720 之后还按老尺寸显示，
        // 角色会凭空小 15%。必须靠帧表里的 characterHeight 把框反过来放大。
        Section("动态素材：显示框高度（换素材不能改变角色大小）");

        var box = sheet.DisplayBoxHeight(AnimLibrary.FloatingCharacterDesignHeight);
        Check(box.HasValue && Math.Abs(box.Value - 198) < 0.6,
            $"720 画布、角色 600 高 → 鲸鱼框应为约 198px（实际 {box?.ToString("0.#") ?? "null"}）");

        if (box.HasValue)
        {
            double onScreen = box.Value * sheet.CharacterHeight / (double)sheet.FrameHeight;
            Check(Math.Abs(onScreen - AnimLibrary.FloatingCharacterDesignHeight) < 0.6,
                $"换算回屏幕，角色仍是 {AnimLibrary.FloatingCharacterDesignHeight:0}px（实际 {onScreen:0.#}）");
        }

        // ------------------------------------------------------------------
        Section("动态素材：坏帧表要给出能看懂的原因");

        void Reject(string label, string json, string expectFragment)
        {
            var r = AnimSheet.Parse(json);
            Check(!r.Ok && r.Error.Contains(expectFragment, StringComparison.Ordinal),
                $"{label} → {(r.Ok ? "竟然通过了" : r.Error)}");
        }

        Reject("缺 count", """{"sheet":"s.png","frameWidth":720,"frameHeight":720,"cols":4,"rows":2}""",
            "count");
        Reject("网格放不下 9 帧", """{"sheet":"s.png","frameWidth":720,"frameHeight":720,"cols":4,"rows":2,"count":9}""",
            "放不下");
        Reject("sheet 里带路径", """{"sheet":"sub/s.png","frameWidth":720,"frameHeight":720,"cols":4,"rows":2,"count":1}""",
            "不能带路径");
        Reject("type=sequence 暂不支持", """{"type":"sequence","sheet":"s.png","frameWidth":1,"frameHeight":1,"cols":1,"rows":1,"count":1}""",
            "sequence");
        Reject("帧尺寸为 0", """{"sheet":"s.png","frameWidth":0,"frameHeight":720,"cols":4,"rows":2,"count":1}""",
            "帧尺寸不合法");
        Reject("anchor 跑到画布外", """{"sheet":"s.png","frameWidth":720,"frameHeight":720,"cols":1,"rows":1,"count":1,"anchor":{"x":9999,"y":660}}""",
            "之外");
        Reject("不是 JSON", "{ 这不是 json", "不是合法的 JSON");
        Reject("顶层是数组", "[1,2,3]", "必须是一个对象");

        // ------------------------------------------------------------------
        Section("动态素材：能救回来的问题只提示、不报废");

        var noFps = AnimSheet.Parse("""{"sheet":"s.png","frameWidth":720,"frameHeight":720,"cols":1,"rows":1,"count":1}""");
        Check(noFps.Ok && noFps.Sheet!.Fps == AnimSheet.DefaultFps &&
              noFps.Warnings.Any(w => w.Contains("fps", StringComparison.Ordinal)),
            "没写 fps → 按默认 24 播，并提示一句");

        var fastFps = AnimSheet.Parse("""{"sheet":"s.png","frameWidth":720,"frameHeight":720,"cols":1,"rows":1,"count":1,"fps":240}""");
        Check(fastFps.Ok && Math.Abs(fastFps.Sheet!.Fps - AnimSheet.MaxFps) < 1e-9,
            $"fps 写太大 → 夹到 {AnimSheet.MaxFps:0}（实际 {fastFps.Sheet?.Fps:0.##}）");

        var sparse = AnimSheet.Parse("""{"sheet":"s.png","frameWidth":720,"frameHeight":720,"cols":4,"rows":2,"count":6}""");
        Check(sparse.Ok && sparse.Warnings.Any(w => w.Contains("多出来", StringComparison.Ordinal)),
            "网格比帧数多 → 允许，但提示多出来的格子不会播");

        var noAnchor = AnimSheet.Parse("""{"sheet":"s.png","frameWidth":720,"frameHeight":720,"cols":1,"rows":1,"count":1}""");
        Check(noAnchor.Ok &&
              noAnchor.Sheet!.AnchorX == 360 && noAnchor.Sheet.AnchorY == 720 &&
              noAnchor.Warnings.Any(w => w.Contains("anchor", StringComparison.Ordinal)),
            "没写 anchor → 按横向居中、脚底贴下边，并提示一句");

        var noChar = AnimSheet.Parse("""{"sheet":"s.png","frameWidth":720,"frameHeight":720,"cols":1,"rows":1,"count":1}""");
        Check(noChar.Ok && !noChar.Sheet!.HasCharacterHeight && noChar.Sheet.DisplayBoxHeight(165) == null,
            "没写 characterHeight → 算不出框高，明确返回 null 让界面自己决定");

        // 手写帧表很容易带上注释和尾逗号，不该因此整份报废
        var lenient = AnimSheet.Parse("""
        {
          // 这是注释
          "sheet": "s.png", "frameWidth": 720, "frameHeight": 720,
          "cols": 1, "rows": 1, "count": 1,
        }
        """);
        Check(lenient.Ok, "带注释和尾逗号的帧表也能解析" + (lenient.Ok ? "" : "：" + lenient.Error));

        // ------------------------------------------------------------------
        Section("动态素材：动作名归一化");

        CheckEq(AnimActions.Canonical("clickBounce"), AnimActions.Click, "clickBounce 本身");
        CheckEq(AnimActions.Canonical("Click_Bounce"), AnimActions.Click, "Click_Bounce（大小写+下划线）");
        CheckEq(AnimActions.Canonical("click"), AnimActions.Click, "click 是别名");
        CheckEq(AnimActions.Canonical("Idle"), AnimActions.Blink, "Idle 归到 blink");
        CheckEq(AnimActions.Canonical("blink-idle"), AnimActions.Blink, "blink-idle 归到 blink");
        CheckEq(AnimActions.Canonical("Reminder"), AnimActions.Alert, "Reminder 归到 alert");
        CheckEq(AnimActions.Canonical("  myCustom  "), "myCustom", "认不出来的动作原样保留（去空格）");
        Check(!AnimActions.IsKnown("myCustom"), "自定义动作不会被界面主动播放");

        // ------------------------------------------------------------------
        Section("动态素材：目录扫描");

        var assetDir = Path.Combine(workspace, "anim-assets");
        var animDir = Path.Combine(assetDir, AnimLibrary.FolderName);

        var missing = AnimLibrary.Scan(assetDir);
        Check(missing.IsEmpty && missing.Problems.Count == 0,
            "素材目录还不存在时安静地退回静态形象（不算错误）");

        Directory.CreateDirectory(animDir);

        // 一个正常动作 + 同画布的静态本体
        AddAction(animDir, "clickBounce", SampleSheetJson, withSheetPng: true);
        File.WriteAllText(Path.Combine(animDir, AnimLibrary.BaseImageName), "not a real png");
        // 动作名归一化之后要和 clickBounce 撞车，只认先扫到的
        AddAction(animDir, "Click-Bounce", SampleSheetJson, withSheetPng: true);
        // 目录名写成下划线形式，也该归到 blink
        AddAction(animDir, "Blink_Idle", SampleSheetJson, withSheetPng: true);
        // 帧表在、精灵图不在
        AddAction(animDir, "halfBaked", SampleSheetJson, withSheetPng: false);
        // 帧表本身是坏的
        AddAction(animDir, "broken", "{ 这不是 json", withSheetPng: true);
        // 目录里连帧表都没有
        Directory.CreateDirectory(Path.Combine(animDir, "emptyDir"));

        var lib = AnimLibrary.Scan(assetDir);

        CheckEq(string.Join(",", lib.ActionNames), $"{AnimActions.Blink},{AnimActions.Click}",
            "只认能用的动作，且名字已归一化");
        Check(lib.Find("clickbounce") != null && lib.Find("CLICK") != null,
            "动作名不区分大小写、也认别名");
        Check(lib.BaseImagePath != null, "anim\\base.png 被认成静态本体");

        var box2 = lib.DisplayBoxHeight(AnimLibrary.FloatingCharacterDesignHeight);
        Check(box2.HasValue && Math.Abs(box2.Value - 198) < 0.6,
            $"扫描结果给出的鲸鱼框高度：{box2?.ToString("0.#") ?? "null"}");

        Check(lib.Problems.Any(p => p.Contains("找不到精灵图", StringComparison.Ordinal)),
            "缺精灵图的动作被记下来：" + string.Join(" / ", lib.Problems));
        Check(lib.Problems.Any(p => p.Contains("不是合法的 JSON", StringComparison.Ordinal)),
            "坏帧表被记下来，且不影响同目录下别的动作");
        Check(lib.Problems.Any(p => p.Contains("没有帧表", StringComparison.Ordinal)),
            "连帧表都没有的目录被记下来");
        Check(lib.Problems.Any(p => p.Contains("重复", StringComparison.Ordinal)),
            "动作名撞车的第二份被跳过并说明原因");

        // 精灵图路径要落到动作自己的目录里 —— 拼错就会"素材在但不生效"
        var sheetPath = lib.SheetImagePath(AnimActions.Click);
        Check(sheetPath != null &&
              Path.GetDirectoryName(sheetPath)!.EndsWith("clickBounce", StringComparison.Ordinal) &&
              File.Exists(sheetPath),
            $"精灵图路径指向动作自己的目录：{sheetPath}");

        // 撞车时该让"名字就是动作键"的那份赢，而不是字母序更靠前的那个
        Check(sheetPath != null && Path.GetDirectoryName(sheetPath)!.EndsWith("clickBounce", StringComparison.Ordinal),
            "同名撞车时，目录名与动作键一致的那份优先于 Click-Bounce");

        Check(lib.Describe().Contains("启用", StringComparison.Ordinal),
            "诊断报告能说清认出了几个动作");

        // 扫描不应该把文件写坏、也不该依赖调用方先建目录
        Check(!lib.IsEmpty && lib.Find(AnimActions.Alert) == null,
            "没放的 alert 动作就是没有（不会凭空多出来）");
    }

    /// <summary>在扫描测试里造一个动作目录。</summary>
    private static void AddAction(string animDir, string name, string manifest, bool withSheetPng)
    {
        var dir = Path.Combine(animDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, AnimLibrary.ManifestName), manifest);

        if (withSheetPng)
            File.WriteAllText(Path.Combine(dir, "sheet.png"), "not a real png");
    }

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine("== " + name);
    }
    private static void Check(bool ok, string description)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  ✓ " + description);
        }
        else
        {
            Failures.Add(description);
            Console.WriteLine("  ✗ " + description);
        }
    }

    private static void CheckEq(string? actual, string expected, string description)
        => Check(actual == expected, $"{description}：期望「{expected}」，实际「{actual ?? "null"}」");

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* 临时目录删不掉就留着，不影响测试结论 */ }
    }
}
