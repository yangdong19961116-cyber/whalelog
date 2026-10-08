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
            TestDayIndex();
            TestSettingsClamp();
            TestSettingsMigration();
            TestIntakeSummary();
            TestAssetCatalog(workspace);
            TestCommandLine();
            TestAnimAssets(workspace);
            TestVideoAssets(workspace);
            TestCharacterGeometry();
            TestCharacterPacks(workspace);
            TestAmbientPicker();
            await TestEndToEndIntakeAsync(workDir);
            await TestDataDirSiblingNotSkippedAsync();
            await TestDateScopedIntakeAsync(workspace);
            await TestVersionAcrossDaysAsync(workspace);
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
    /// 跨天再归档：同一个文件前一天归档过，今天改了内容再归档一次。
    ///
    /// 这是用户实际报上来的问题：9 月 16 日归档的文件，9 月 17 日再归档时
    /// 只在 9 月 16 日那页看到 v2，9 月 17 日那页什么都没有。
    /// 根因是升版本时只改了版本号、没动归属日期，而全程序判断
    /// "某天有哪些归档文件"只看归属日期。
    ///
    /// 期望：两个动作各留一条 —— 前一天那页是 v1，今天那页是 v2。
    /// </summary>
    private static async Task TestVersionAcrossDaysAsync(string workspace)
    {
        Section("跨天再归档");

        var dir = Path.Combine(workspace, "across-days");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "模型.blend");

        var settings = new SettingsService();
        settings.Load();
        settings.Update(s => s.SnapshotMode = false);

        var store = new LogBookStore();
        store.Load();
        var classify = new ClassifyService();
        classify.Load();
        var intake = new FileIntakeService(store, classify, new VersionService(settings), settings);

        var day0 = DateTime.Today.AddDays(-2);
        var day1 = DateTime.Today.AddDays(-1);
        var day2 = DateTime.Today;
        var key0 = day0.ToString("yyyy-MM-dd");
        var key1 = day1.ToString("yyyy-MM-dd");
        var key2 = day2.ToString("yyyy-MM-dd");

        // --- 前一天：首次归档 ---
        File.WriteAllText(file, "第一版正文");
        await intake.IntakeAsync(new[] { file }, day1);

        var first = store.FindFileByPathOn(file, key1);
        Check(first != null && first.Version == 1,
            $"前一天归档应记成 v1，实际 {(first == null ? "没有属于那天的记录" : "v" + first.Version)}");

        // --- 今天：改了内容再归档 ---
        File.WriteAllText(file, "第二版正文：补了细节");
        var second = await intake.IntakeAsync(new[] { file }, day2);
        Check(second.Count == 1 && second[0].Status == IntakeStatus.NewVersion,
            $"内容变了应判为新版本，实际 {(second.Count == 1 ? second[0].Status.ToString() : second.Count + " 条结果")}");

        var today = store.FindFileByPathOn(file, key2);
        Check(today != null,
            "今天应该多出一条属于今天的记录 —— 以前缺的就是这条，于是今天的页面空着");
        Check(today?.Version == 2, $"今天这条应是 v2，实际 v{today?.Version}");
        Check(store.FindFileByPathOn(file, key1)?.Version == 1,
            $"昨天那条应该仍停在 v1，不该被今天这次顶上去，实际 v{store.FindFileByPathOn(file, key1)?.Version}");

        // 下面两条就是用户看到的现象本身：翻到哪天，就该看到哪天归档的东西
        Check(store.FilesOnDate(day2).Any(f => f.FullPath == file),
            "「只看当天」看今天应看得到它");
        Check(store.FilesOnDate(day1).Any(f => f.FullPath == file),
            "看昨天应仍然看得到昨天那一条");
        Check(store.FilesOnDate(day2).Count(f => f.FullPath == file) == 1,
            "今天只该有一条");

        // --- 同一天里再改一次：只升当天那条的版本号，不另起一行 ---
        File.WriteAllText(file, "第三版正文：又改了一次");
        await intake.IntakeAsync(new[] { file }, day2);
        Check(store.FilesOnDate(day2).Count(f => f.FullPath == file) == 1,
            $"同一天里反复改，当天仍只该有一行，实际 {store.FilesOnDate(day2).Count(f => f.FullPath == file)} 行");
        Check(store.FindFileByPathOn(file, key2)?.Version == 3,
            $"当天那条的版本号应顶到 v3，实际 v{store.FindFileByPathOn(file, key2)?.Version}");

        // --- 版本链：两条记录，各管各的版本号 ---
        var chain = store.VersionChain(today!.VersionGroupKey);
        Check(chain.Count == 2, $"版本链上应是两条记录（各一天的归档动作），实际 {chain.Count}");
        Check(chain.Select(c => c.Version).SequenceEqual(new[] { 1, 3 }),
            $"版本号应是 v1 → v2 → v3 递增、两条记录分别落在 v1 和 v3，实际 {string.Join(",", chain.Select(c => "v" + c.Version))}");

        // --- 更早的一天再拖一次，但内容没变：不该凭空多出一条 ---
        var third = await intake.IntakeAsync(new[] { file }, day0);
        Check(third.Count == 1 && third[0].Status == IntakeStatus.Unchanged,
            $"内容没变时应判为未产生新版本，实际 {(third.Count == 1 ? third[0].Status.ToString() : third.Count + " 条结果")}");
        Check(store.FindFileByPathOn(file, key0) == null,
            "内容没变时不该凭空多出一条当天的记录");

        // --- 快照跟着**这一版归档的那天**放 ---
        settings.Update(s => s.SnapshotMode = true);
        File.WriteAllText(file, "第四版正文：开了快照再改一次");
        await intake.IntakeAsync(new[] { file }, day2);

        var snapped = store.FindFileByPathOn(file, key2);
        Check(snapped?.Version == 4, $"应继续升到 v4，实际 v{snapped?.Version}");
        Check(snapped?.SnapshotPath != null && snapped.SnapshotPath.Contains(key2),
            $"快照应放在当天那个目录下，实际 {snapped?.SnapshotPath ?? "（没有快照）"}");
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

        // 截图开关是一串"前缀相同、后缀不同"的开关，全靠 Has 的完全匹配分家。
        // 认错的后果不是崩溃，而是跑完没反应、或者出图出成了另一页 —— 看日志看不出来。
        var calendar = new CommandLine(new[] { "--shot-calendar", "D:\\tmp\\cal.png" });
        Check(!calendar.Has("--shot"), "`--shot-calendar` 不该被当成 `--shot`");
        CheckEq(calendar.Value("--shot-calendar"), @"D:\tmp\cal.png", "月历截图开关应能取到值");

        var fileCalendar = new CommandLine(new[] { "--shot-file-calendar", "D:\\tmp\\fcal.png" });
        Check(!fileCalendar.Has("--shot"), "`--shot-file-calendar` 不该被当成 `--shot`");
        Check(!fileCalendar.Has("--shot-calendar"), "`--shot-file-calendar` 也不该被当成 `--shot-calendar`");
        CheckEq(fileCalendar.Value("--shot-file-calendar"), @"D:\tmp\fcal.png", "归档日历截图开关应能取到值");

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
    /// 「哪天有记录」的索引，以及归档文件的按关键字筛选。
    ///
    /// 这两块都是"回看往期"的地基：索引错了，往期清单和月历小圆点会各说各话；
    /// 筛选错了，用户搜不到自己明明归档过的文件。两者都不报错，只是安静地少给东西，
    /// 所以边界（没有日期的记录、空白关键字、越过头尾）要一条条钉住。
    /// </summary>
    private static void TestDayIndex()
    {
        Section("按日期回看：索引与搜索");

        var today = new DateTime(2026, 10, 8);   // 周四

        var days = new List<DayNote>
        {
            new()
            {
                Date = "2026-09-24",
                Entries =
                {
                    new NoteEntry { Text = "整理第三季度数据", Done = true },
                    new NoteEntry { Text = "写周报", Done = false },
                },
            },
            new() { Date = "2026-09-23", Entries = { new NoteEntry { Text = "需求评审" } } },
            // 这一天在 Book 里存在（启动时 EnsureToday 造的），但既没记录也没文件 ——
            // 不该进索引，否则往期清单里会多出一行"9月20日 没有内容"。
            new() { Date = "2026-09-20" },
        };

        static FileRecord F(string name, string date, string category = "文档") => new()
        {
            FileName = name,
            AddedDate = date,
            Category = category,
            FullPath = @"C:\work\" + name,
        };

        var files = new List<FileRecord>
        {
            F("Q3报表.xlsx", "2026-09-24", "表格"),
            F("Q3报表.xlsx", "2026-09-24", "表格"),
            F("会议纪要.docx", "2026-09-23"),
            F("没有归属日期.bin", ""),
        };

        var digests = DayIndex.Build(days, files);

        Check(digests.Count == 2,
            $"只有 09-24 / 09-23 这两天有内容，实际 {digests.Count} 天：{string.Join("、", digests.Select(d => d.Date))}");

        Check(digests.Select(d => d.Date).SequenceEqual(new[] { "2026-09-24", "2026-09-23" }),
            $"索引必须从新到旧，实际 {string.Join(" → ", digests.Select(d => d.Date))}");

        var first = digests[0];
        Check(first.Entries == 2 && first.Open == 1 && first.Files == 2,
            $"09-24 应有 2 条记录（1 条未收尾）+ 2 个文件，实际 {first.Entries}/{first.Open}/{first.Files}");
        Check(first.Total == 4, $"合计应为 4，实际 {first.Total}");

        Check(digests.All(d => d.HasContent),
            "索引里不该出现总数为 0 的日子");
        Check(digests.Sum(d => d.Files) == 3,
            $"没有归属日期的归档记录不进索引（3 个有日期的文件才对），实际 {digests.Sum(d => d.Files)}");

        Check(DayIndex.CountsText(first) == "记录 2 · 文件 2",
            $"实际「{DayIndex.CountsText(first)}」");
        Check(DayIndex.CountsText(new DayDigest { Date = "2026-09-23", Files = 1 }) == "文件 1",
            "只有文件时不该带出「记录 0」");

        // ---- 搜索 ----
        Check(DayIndex.Search(digests, "").Count == 2, "空关键字应当原样返回（不筛）");
        Check(DayIndex.Search(digests, "   ").Count == 2, "全空白和没输入是同一件事");

        Check(DayIndex.Search(digests, "周报").Single().Date == "2026-09-24",
            "记录正文里的词要能搜到");
        Check(DayIndex.Search(digests, "会议").Single().Date == "2026-09-23",
            "归档文件名里的词也要能搜到");
        Check(DayIndex.Search(digests, "第三季度").Count == 1, "长词整体匹配");
        Check(DayIndex.Search(digests, "不存在的东西").Count == 0, "搜不到时应给空结果，不是全部");
        Check(DayIndex.Search(digests, "q3").Single().Date == "2026-09-24",
            "大小写不敏感");

        Check(DayIndex.FirstMatch(first, "周报") == "写周报",
            $"命中说明要指出是哪一条，实际「{DayIndex.FirstMatch(first, "周报") ?? "null"}」");
        Check(DayIndex.FirstMatch(first, "") == null, "没搜索时不给命中说明");

        // ---- Alt+← / → 的落点 ----
        Check(DayIndex.Adjacent(digests, new DateTime(2026, 10, 8), -1) == new DateTime(2026, 9, 24),
            "从今天往前走，应落到最近的一个有记录的日子（跨过中间那些空白日）");
        Check(DayIndex.Adjacent(digests, new DateTime(2026, 9, 24), -1) == new DateTime(2026, 9, 23),
            "再往前应落到 09-23");
        Check(DayIndex.Adjacent(digests, new DateTime(2026, 9, 23), -1) == null,
            "没有更早的了要给 null（界面据此说一句，而不是原地打转）");
        Check(DayIndex.Adjacent(digests, new DateTime(2026, 9, 22), 1) == new DateTime(2026, 9, 23),
            "往后走应落到 09-23");
        Check(DayIndex.Adjacent(digests, new DateTime(2026, 9, 24), 1) == null,
            "已经是最后一天时往后没有落点");
        Check(DayIndex.Adjacent(digests, new DateTime(2026, 9, 24), 0) == null,
            "方向 0 既不是往前也不是往后，给 null 而不是随便挑一天");

        // ---- 文案 ----
        Check(DayIndex.DateLabel("2026-09-24", today) == "9月24日",
            $"当年不带年份，实际「{DayIndex.DateLabel("2026-09-24", today)}」");
        Check(DayIndex.DateLabel("2025-09-24", today) == "2025年9月24日",
            $"跨年必须带年份，实际「{DayIndex.DateLabel("2025-09-24", today)}」");
        Check(DayIndex.WeekdayLabel("2026-10-08", today) == "今天",
            $"实际「{DayIndex.WeekdayLabel("2026-10-08", today)}」");
        Check(DayIndex.WeekdayLabel("2026-10-07", today) == "昨天",
            $"实际「{DayIndex.WeekdayLabel("2026-10-07", today)}」");
        Check(DayIndex.WeekdayLabel("2026-09-24", today) == "周四",
            $"更早的日期显示星期，实际「{DayIndex.WeekdayLabel("2026-09-24", today)}」");
        Check(DayIndex.MonthLabel("2026-09-24", today) == "9月",
            $"同年的月份头不带年份，实际「{DayIndex.MonthLabel("2026-09-24", today)}」");
        Check(DayIndex.MonthLabel("2025-09-24", today) == "2025 年 9 月",
            $"跨年的月份头要带年份，实际「{DayIndex.MonthLabel("2025-09-24", today)}」");
        Check(DayIndex.SummaryText(digests) == "2 天有记录 · 3 条记录 · 3 个文件",
            $"实际「{DayIndex.SummaryText(digests)}」");
        Check(DayIndex.SummaryText(new List<DayDigest>()) == "还没有可回溯的记录",
            $"空索引要给一句人话，实际「{DayIndex.SummaryText(new List<DayDigest>())}」");

        // ---- 归档计数（文件归档页日历上的蓝点 + 小数字） ----
        var counts = DayIndex.ArchiveCounts(files);

        Check(counts.Count == 2, $"只有标了日期的文件进计数，实际 {counts.Count} 天");
        Check(counts["2026-09-24"] == 2 && counts["2026-09-23"] == 1,
            $"每天的数量要对，实际 09-24={counts.GetValueOrDefault("2026-09-24")}、"
            + $"09-23={counts.GetValueOrDefault("2026-09-23")}");
        Check(!counts.ContainsKey(""),
            "没标日期的文件不给空字符串键 —— 日历上没有它的落点，硬塞进去会多出一格永远点不动的数字");

        Check(counts["2026-09-24"] == first.Files,
            $"归档计数必须和索引里的文件数同源（{counts["2026-09-24"]} vs {first.Files}）—— "
            + "各算一份就会出现「日历说 3 个、清单说 4 个」");

        Check(DayIndex.ArchiveCounts(null).Count == 0 && DayIndex.ArchiveCounts(new List<FileRecord>()).Count == 0,
            "没有文件时给空表，不能抛");

        Check(DayIndex.CountBadge(0) == "" && DayIndex.CountBadge(-1) == "",
            "没有归档的日子不写数字（空串），而不是写个 0");
        Check(DayIndex.CountBadge(1) == "1" && DayIndex.CountBadge(42) == "42" && DayIndex.CountBadge(99) == "99",
            "一两位原样写");
        Check(DayIndex.CountBadge(100) == "99+" && DayIndex.CountBadge(4321) == "99+",
            $"三位数截成 99+（格子只有 36 宽，撑出去会被布局套一个矩形 clip 把圆角切平），"
            + $"实际「{DayIndex.CountBadge(100)}」");

        // ---- 归档页的搜索 ----
        Check(FileSearch.Filter(files, "").Count == files.Count, "空关键字不筛");
        Check(FileSearch.Filter(files, "   ").Count == files.Count, "全空白不筛");
        Check(FileSearch.Filter(files, "Q3报表").Count == 2, "按文件名筛");
        Check(FileSearch.Filter(files, "  Q3报表  ").Count == 2, "关键字首尾空白要吃掉");
        Check(FileSearch.Filter(files, "表格").Count == 2, "按类别筛");
        Check(FileSearch.Filter(files, @"C:\work").Count == 4, "按路径筛");
        Check(FileSearch.Filter(files, "q3").Count == 2, "文件名匹配大小写不敏感");
        Check(FileSearch.Filter(files, "彻底不存在").Count == 0, "筛不到就是空列表");

        var withNote = F("报表.xlsx", "2026-09-24");
        withNote.Note = "给老板的版本";
        withNote.MatchedRule = "表格类";
        Check(FileSearch.Matches(withNote, "老板"), "备注也要能搜到");
        Check(FileSearch.Matches(withNote, "表格类"), "命中的规则名也要能搜到");
        Check(!FileSearch.Matches(withNote, withNote.Id), "内部 Id 不该被搜到（用户看不见它）");
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

    /// <summary>
    /// 设置文件的一次性迁移。
    ///
    /// 要钉住的规则：`autoOpenMainWindowOnDrop` 在 1.4.3 及以前是个**死字段**
    /// （只写不读），磁盘上那个 `false` 不代表用户在"拖文件到小鲸鱼上要不要弹主面板"
    /// 这件事上做过任何选择。1.4.4 起它真的管事了，这时必须替老配置打开一次，
    /// 否则老用户升级上来会看到"拖上去什么都没发生"、而配置文件里那一行看着毫无异常。
    ///
    /// 反过来也要钉住：**用户自己选过的值不许被覆盖** —— 迁移只认代次，不认内容。
    /// </summary>
    private static void TestSettingsMigration()
    {
        Section("设置：一次性迁移");

        // 第 1 代的文件：没有 settingsRevision，且死字段是 false
        File.WriteAllText(AppPaths.SettingsFile,
            """{"autoOpenMainWindowOnDrop": false, "floatingScale": 1.2}""");

        var old = new SettingsService();
        old.Load();
        Check(old.Settings.AutoOpenMainWindowOnDrop,
            "老配置（1.4.3 及以前）升级上来时必须把「拖到小鲸鱼上自动打开主面板」打开一次");
        Check(old.Settings.SettingsRevision >= 2,
            $"迁移后应记下代次，实际 {old.Settings.SettingsRevision}");
        Check(Math.Abs(old.Settings.FloatingScale - 1.2) < 1e-9,
            "迁移不该顺手改别的字段");

        // 第 2 代的文件：用户自己关掉的，必须原样尊重
        File.WriteAllText(AppPaths.SettingsFile,
            """{"autoOpenMainWindowOnDrop": false, "settingsRevision": 2}""");

        var newer = new SettingsService();
        newer.Load();
        Check(!newer.Settings.AutoOpenMainWindowOnDrop,
            "已经是新一代配置、用户自己关掉的开关，绝不能又被迁移打开");

        try { File.Delete(AppPaths.SettingsFile); } catch { /* 删不掉就算了 */ }
    }

    /// <summary>
    /// 归档结果的文案。
    ///
    /// 悬浮窗的气泡和主面板左下角的状态栏共用这一句 —— 两处各写一份的话，
    /// 同一次拖入在两个地方会显示成两种说法。
    /// </summary>
    private static void TestIntakeSummary()
    {
        Section("归档：结果文案");

        static IntakeResult R(IntakeStatus s) => new() { Status = s };

        var mixed = new List<IntakeResult>
        {
            R(IntakeStatus.Added), R(IntakeStatus.Added),
            R(IntakeStatus.NewVersion),
            R(IntakeStatus.Duplicate),
            R(IntakeStatus.Failed),
        };

        var text = FileIntakeService.Summarize(mixed);
        CheckEq(text, "新增 2，新版本 1，重复 1，失败 1", "五个状态合起来");

        CheckEq(FileIntakeService.Summarize(Array.Empty<IntakeResult>()),
            "没有可归档的文件", "一条结果都没有时");

        CheckEq(FileIntakeService.Summarize(new[] { R(IntakeStatus.Added) }, "（记入 9月16日）"),
            "新增 1（记入 9月16日）", "后缀要接在最后");

        var unchangedOnly = FileIntakeService.Summarize(new[] { R(IntakeStatus.Unchanged) });
        Check(!unchangedOnly.Contains("新增"), $"只读的状态不该冒出「新增」：实际「{unchangedOnly}」");

        // ---- "真收下了几个" ----
        // 悬浮窗靠这个数决定"要不要演清点归档"（见 FloatingWindow.PlayArchiveCheck）。
        // 数错的表现是"拖一份重复文件它也蹦起来演"—— 用户会以为程序没在干活。
        CheckEq(FileIntakeService.AcceptedCount(mixed).ToString(), "3",
            "真收下的 = 新增 + 新版本（重复和失败不算）");

        CheckEq(FileIntakeService.AcceptedCount(new[]
        {
            R(IntakeStatus.Duplicate), R(IntakeStatus.Unchanged), R(IntakeStatus.Failed),
        }).ToString(), "0", "全是重复 / 内容未变 / 失败时一个都没收下");

        CheckEq(FileIntakeService.AcceptedCount(Array.Empty<IntakeResult>()).ToString(), "0",
            "一条结果都没有时也是 0（不庆祝）");

        Check(new FileIntakeService.IntakeOutcome("新增 1", 1).HasNews,
            "有新增 → 值得庆祝");
        Check(!new FileIntakeService.IntakeOutcome("重复 1", 0).HasNews,
            "只有重复 → 不庆祝（气泡照旧会说「重复 1」，用户不会觉得没反应）");
    }

    /// <summary>
    /// 归档会跳过"自己的数据目录"（免得把 index.json、快照副本又拖回来归档）。
    ///
    /// 判断用的是路径前缀。只比目录名本身的话，名字**以数据目录名开头**的兄弟目录
    /// （数据目录是 data、拖的是 data-备份）会被整批误跳过；而且是静默的 ——
    /// 拖一个文件夹进来，一个文件都收不上，界面只说"没有可归档的文件"，
    /// 看不出是被过滤掉了。
    /// </summary>
    private static async Task TestDataDirSiblingNotSkippedAsync()
    {
        Section("归档：只跳过自己的数据目录");

        var root = AppPaths.Root;
        var sibling = root + "-兄弟";

        Directory.CreateDirectory(sibling);
        Directory.CreateDirectory(root);

        var file = Path.Combine(sibling, "兄弟目录里的文件.txt");
        File.WriteAllText(file, "不该被当成数据目录里的文件");

        var settings = new SettingsService();
        settings.Load();
        settings.Update(s => s.SnapshotMode = false);

        var store = new LogBookStore();
        store.Load();
        var classify = new ClassifyService();
        classify.Load();
        var intake = new FileIntakeService(store, classify, new VersionService(settings), settings);

        var results = await intake.IntakeAsync(new[] { file });
        var record = store.FindFileByPath(file);

        Check(record != null,
            $"数据目录的兄弟目录（只差一个后缀）里的文件应该能归档：" +
            $"拿到 {results.Count} 条结果，记录{(record == null ? "没" : "")}落库");

        Check(results.All(r => r.Status != IntakeStatus.Nothing),
            "不该被判成「没有可归档的文件」—— 那正是前缀误判时的表现");

        // 反过来：数据目录里面的文件仍然必须跳过
        var skipped = await intake.IntakeAsync(new[] { Path.Combine(root, "index.json") });
        var skippedStatus = string.Join("、", skipped.Select(r => r.Status.ToString()));
        Check(skipped.All(r => r.Status == IntakeStatus.Nothing),
            $"数据目录**里面**的文件仍然要跳过（否则会把索引和快照副本又拖回来归档）：实际判成 {skippedStatus}");
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
        Section("动态素材：角色识别（界面靠它决定播哪个动作）");

        // 一个形象可能有一百多个动作，界面必须知道"点击该播哪些""闲下来该冒什么"。
        // 角色只认目录名的第一段，所以同一个角色的多个动作靠后缀区分。
        CheckEq(AnimActions.RoleOf("idle-待机呼吸休闲"), AnimActions.Idle, "idle-* → 待机");
        CheckEq(AnimActions.RoleOf("clickBounce-开心跃动"), AnimActions.Click, "clickBounce-* → 点击回应");
        CheckEq(AnimActions.RoleOf("ambient-写代码"), AnimActions.Ambient, "ambient-* → 闲置小动作");
        CheckEq(AnimActions.RoleOf("ambient-余额-钱袋满溢"), AnimActions.Ambient, "多段后缀也认第一段");
        CheckEq(AnimActions.RoleOf("drag-被鼠标拖拽悬空反馈"), AnimActions.Drag, "drag-* → 拖拽");
        CheckEq(AnimActions.RoleOf("move-左转奔跑"), AnimActions.Move, "move-* → 滑动");
        CheckEq(AnimActions.RoleOf("turn-东张西望"), AnimActions.Turn, "turn-* → 转向");
        CheckEq(AnimActions.RoleOf("blink"), AnimActions.Blink, "blink 单独一个目录也是角色");
        CheckEq(AnimActions.RoleOf("archive-清点归档"), AnimActions.Archive,
            "archive-* → 归档成功后的清点");
        CheckEq(AnimActions.RoleOf("cheer-雀跃庆祝"), AnimActions.Cheer,
            "cheer-* → 完工庆祝（下班 / 今天最后一条收尾）");
        // 老素材的前缀：这两个动画以前是连播成一套的（celebrate-1-清点 → celebrate-2-庆祝），
        // 拆成两个角色之后，旧前缀归到语义最接近的 cheer —— 不认的话它们会掉成
        // "认不出角色"，界面上再也演不出来，而且不报任何错。
        CheckEq(AnimActions.RoleOf("celebrate-2-雀跃庆祝"), AnimActions.Cheer,
            "旧的 celebrate-* 前缀仍归到完工庆祝（不能掉成认不出）");
        CheckEq(AnimActions.RoleOf("随手动"), "", "认不出角色 → 空串（用户自制动作不会被主动播）");

        // 这一条是那次踩过的坑：目录名 idle 归一化之后动作键变成 blink，
        // 如果角色从动作键反推就会把它认成"眨眼" —— 而它其实是要一直循环播的待机。
        CheckEq(AnimActions.Canonical("idle"), AnimActions.Blink, "目录名 idle 的动作键仍是 blink（历史行为）");
        CheckEq(AnimActions.RoleOf("idle"), AnimActions.Idle, "但它的角色必须是待机（不能从动作键反推）");

        Check(AnimActions.IsLoopingRole(AnimActions.Idle) && AnimActions.IsLoopingRole(AnimActions.Drag),
            "待机与拖拽是一直循环的角色");
        Check(!AnimActions.IsLoopingRole(AnimActions.Click) && !AnimActions.IsLoopingRole(AnimActions.Ambient),
            "点击与闲置小动作是一次性的（播完要回到待机）");
        // 归档和完工这两个都不能循环：循环就永远播不完，表现是"蹦起来之后再也不回待机"。
        Check(!AnimActions.IsLoopingRole(AnimActions.Archive) && !AnimActions.IsLoopingRole(AnimActions.Cheer),
            "清点归档和完工庆祝都是一次性的（循环的话永远回不到待机）");

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

        // 素材路径要落到动作自己的目录里 —— 拼错就会"素材在但不生效"。
        // 统一叫 MediaPath：精灵图给 PNG、透明视频给 webm，由帧表的 type 决定。
        var sheetPath = lib.MediaPath(AnimActions.Click);
        Check(sheetPath != null &&
              Path.GetDirectoryName(sheetPath)!.EndsWith("clickBounce", StringComparison.Ordinal) &&
              File.Exists(sheetPath),
            $"素材路径指向动作自己的目录：{sheetPath}");

        // 撞车时该让"名字就是动作键"的那份赢，而不是字母序更靠前的那个
        Check(sheetPath != null && Path.GetDirectoryName(sheetPath)!.EndsWith("clickBounce", StringComparison.Ordinal),
            "同名撞车时，目录名与动作键一致的那份优先于 Click-Bounce");

        Check(lib.Describe().Contains("启用", StringComparison.Ordinal),
            "诊断报告能说清认出了几个动作");

        // 扫描不应该把文件写坏、也不该依赖调用方先建目录
        Check(!lib.IsEmpty && lib.Find(AnimActions.Alert) == null,
            "没放的 alert 动作就是没有（不会凭空多出来）");

        // ---- 两个"演一段"的角色要各归各的 ----
        // 归档 → archive（清点归档）、完工 → cheer（雀跃庆祝）。这两个以前是连播的一套
        // （celebrate-1- / celebrate-2-），拆开之后最容易犯的错是**串了**：
        // 归档完跳起来庆祝、下班时低头清点 —— 界面上只是"看着别扭"，一个字都不报。
        AddAction(animDir, $"{AnimActions.Archive}-清点归档", SampleSheetJson, withSheetPng: true);
        AddAction(animDir, $"{AnimActions.Archive}-再点一遍", SampleSheetJson, withSheetPng: true);
        AddAction(animDir, $"{AnimActions.Cheer}-雀跃庆祝", SampleSheetJson, withSheetPng: true);

        var ordered = AnimLibrary.Scan(assetDir);

        CheckEq(ordered.ActionsWithRole(AnimActions.Archive).Count.ToString(), "2",
            "同角色的多个清点动作都归到一组（界面据此随机挑一个）");
        CheckEq(string.Join("、", ordered.ActionsWithRole(AnimActions.Cheer)),
            $"{AnimActions.Cheer}-雀跃庆祝", "完工角色里只有庆祝，没有把清点捎带进来");
        Check(!ordered.ActionsWithRole(AnimActions.Archive).Contains($"{AnimActions.Cheer}-雀跃庆祝") &&
              !ordered.ActionsWithRole(AnimActions.Cheer).Contains($"{AnimActions.Archive}-清点归档"),
            "两个角色互不重叠（串了就会归档时庆祝、下班时清点）");
        CheckEq(ordered.ActionsWithRole(AnimActions.Ambient).Count.ToString(), "0",
            "这两个角色不会被算成普通闲置小动作（否则平时也会随机蹦出来演）");
        CheckEq(ordered.RoleOf($"{AnimActions.Archive}-清点归档"), AnimActions.Archive,
            "角色从目录名认出来了");
        CheckEq(ordered.RoleOf($"{AnimActions.Cheer}-雀跃庆祝"), AnimActions.Cheer,
            "角色从目录名认出来了");
    }

    // ==================================================================
    // 形象显示几何
    // ==================================================================

    /// <summary>
    /// 两套形象**各按自己画布**算显示框。
    ///
    /// 这里钉的是 2026-09-20 那个故障：显示框高写死 168，两套画布共用一把尺子 ——
    /// dsh（384 画布、角色 270）静态被显示成 116.8px，一开动画跳回 165px，
    /// 每播一次动作形象放大一倍再缩回去。用户的原话是"两套模板冲突"。
    ///
    /// 同类错误还有一条更要命：宽度只能由高度推 = 假定画布是方的。
    /// 16:9 的素材要显示就只能先 crop 成方格 —— 道具被切成平口，
    /// 这就是用户说的"新版显示被裁"。所以宽高比那一条必须钉死。
    /// </summary>
    private static void TestCharacterGeometry()
    {
        Section("形象显示几何：两套画布各算各的");

        var classic = CharacterGeometry.Classic;
        var (cw, ch) = classic.DisplayBox();
        Check(Math.Abs(cw - 167.75) < 0.01 && Math.Abs(ch - 167.75) < 0.01,
            $"原版：610 画布、角色 600 → 框 167.75×167.75（实际 {cw:0.##}×{ch:0.##}）");

        var (lw, lh) = classic.LayoutSize();
        Check(Math.Abs(lw - 236) < 0.01 && Math.Abs(lh - 296) < 0.01,
            $"原版版式仍是 236×296（实际 {lw:0.##}×{lh:0.##}）");

        // 16:9 那套：画布 640×360、角色 270
        var dsh = CharacterGeometry.Parse(
            """{"geometry":{"canvasWidth":640,"canvasHeight":360,"characterHeight":270}}""");
        var (bw, bh) = dsh.DisplayBox();
        Check(Math.Abs(bw - 391.1) < 0.5 && Math.Abs(bh - 220) < 0.5,
            $"dsh：640×360 画布、角色 270 → 框 391.1×220（实际 {bw:0.#}×{bh:0.#}）");

        // ★ 核心不变量：框和画布同比例。挤成方的就必然要裁掉两侧。
        Check(Math.Abs(bw / bh - 640.0 / 360.0) < 0.001,
            $"显示框与画布同比例（{bw / bh:0.####} vs {640.0 / 360.0:0.####}）" +
            " —— 16:9 不会被挤成方的");

        // 两个形象在屏幕上的角色高度必须是同一个数（换形象不该改变大小）
        double onClassic = cw * classic.CharacterHeight / classic.CanvasWidth;
        double onDsh = bh * dsh.CharacterHeight / dsh.CanvasHeight;
        Check(Math.Abs(onClassic - onDsh) < 0.5,
            $"换形象后角色仍是同样大：原版 {onClassic:0.#}px、dsh {onDsh:0.#}px");

        var (dw, dh) = dsh.LayoutSize();
        Check(dw >= bw - 0.01 && dh >= bh + CharacterGeometry.VerticalPadding - 0.01,
            $"窗口装得下 dsh 的框：窗口 {dw:0.#}×{dh:0.#}、框 {bw:0.#}×{bh:0.#}");

        // 版本之间不能互相"借"尺寸：1360 高之类的手写值不该把窗口撑炸
        var huge = CharacterGeometry.Parse(
            """{"geometry":{"canvasWidth":99999,"canvasHeight":360,"characterHeight":270}}""");
        Check(ReferenceEquals(huge, CharacterGeometry.Classic), "画布大得离谱 → 退回原版");

        var bad = new[]
        {
            "",
            "{",
            "{}",
            """{"geometry":{}}""",
            """{"geometry":{"canvasWidth":0,"canvasHeight":360,"characterHeight":270}}""",
            """{"geometry":{"cell":0}}""",
            """{"geometry":{"canvasWidth":99999,"canvasHeight":360,"characterHeight":270}}""",
        };
        int fell = bad.Count(j => ReferenceEquals(CharacterGeometry.Parse(j, "用例"), CharacterGeometry.Classic));
        Check(fell == bad.Length, $"坏几何一律退回原版，不抛：{fell}/{bad.Length}");

        // 老包的 cell 字段要认 —— 否则升级一次就得让所有人重新生成素材
        var legacy = CharacterGeometry.Parse("""{"geometry":{"cell":384,"characterHeight":270}}""");
        Check(legacy.CanvasWidth == 384 && legacy.CanvasHeight == 384 && legacy.CharacterHeight == 270,
            $"老写法 cell 仍被接受（{legacy.CanvasWidth}×{legacy.CanvasHeight}、角色 {legacy.CharacterHeight}）");

        // CharacterHeight 比画布还高是坏数据，按画布高处理（至少不被裁）
        var clamped = CharacterGeometry.Parse(
            """{"geometry":{"canvasWidth":640,"canvasHeight":360,"characterHeight":900}}""");
        Check(!ReferenceEquals(clamped, CharacterGeometry.Classic) && clamped.CharacterHeight == 360,
            $"角色比画布高 → 按画布高处理（{clamped.CharacterHeight}）");
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

    // ==================================================================
    // 透明视频素材
    // ==================================================================

    /// <summary>
    /// 透明视频（dsh 那套素材）的帧表、滤镜校验，以及**真的解一遍**。
    ///
    /// 滤镜串是要原样拼进 ffmpeg 命令行的，帧表又是用户可以手改的文件 ——
    /// 所以它必须被当成不可信输入校验，这里专门钉几条注入用例。
    /// </summary>
    private static void TestVideoAssets(string workspace)
    {
        Section("动态素材：透明视频的帧表");

        const string goodJson = """
        {
          "name": "idle-待机呼吸休闲",
          "type": "video",
          "video": "video.webm",
          "filter": "crop=384:360:128:0,pad=384:384:0:6:color=black@0,crop=384:384:0:0",
          "frameWidth": 384, "frameHeight": 384,
          "cols": 1, "rows": 241, "count": 241, "fps": 24.0, "loop": true,
          "characterHeight": 270,
          "anchor": { "x": 192.0, "y": 336.0 },
          "returnTo": "base"
        }
        """;

        var v = AnimSheet.Parse(goodJson);
        Check(v.Ok, "透明视频的帧表能解析" + (v.Ok ? "" : "：" + v.Error));
        Check(v.Ok && v.Sheet!.IsVideo, "type=video 被认成视频");
        Check(v.Ok && v.Sheet!.MediaFile == "video.webm", "视频动作读的是 video 字段");
        Check(v.Ok && v.Sheet!.Sheet.Length == 0, "视频动作的 sheet 字段留空（不给两条路）");
        Check(v.Ok && v.Sheet!.Filter.StartsWith("crop=", StringComparison.Ordinal), "滤镜串原样保留给运行时用");
        Check(v.Ok && v.Sheet!.Cols == 1 && v.Sheet.Rows == 241,
            $"视频不需要排帧网格，占位给 1×N：{v.Sheet?.Cols}×{v.Sheet?.Rows}");
        Check(v.Ok && v.Warnings.Count == 0, "正常的视频帧表不该产生警告");

        // 视频的帧数允许不写：以实际解出来多少帧为准（生成工具换代时不会因为少个字段就报废）
        var noCount = AnimSheet.Parse("""
        {"type":"video","video":"v.webm","frameWidth":384,"frameHeight":384,"fps":24}
        """);
        Check(noCount.Ok && noCount.Sheet!.Count == 0 &&
              noCount.Warnings.Any(w => w.Contains("count", StringComparison.Ordinal)),
            "视频没写 count 也能用，只提示一句");

        // ------------------------------------------------------------------
        Section("动态素材：视频滤镜的安全校验（防命令注入）");

        // 帧表是用户可改的文件，滤镜串会被原样拼进 ffmpeg 命令行 —— 必须当成不可信输入
        static string FilterOf(string filter)
            => "{\"type\":\"video\",\"video\":\"v.webm\",\"filter\":\"" +
               filter + "\",\"frameWidth\":384,\"frameHeight\":384,\"fps\":24}";

        var drawtext = AnimSheet.Parse(FilterOf("crop=384:360:0:0,drawtext=text=x"));
        Check(!drawtext.Ok && drawtext.Error.Contains("crop / scale / pad", StringComparison.Ordinal),
            "只放行 crop/scale/pad，别的滤镜一律拒绝：" + drawtext.Error);

        var semicolon = AnimSheet.Parse(FilterOf("crop=384:360:0:0;whoami"));
        Check(!semicolon.Ok, "参数里混进分隔符会被拒：" + semicolon.Error);

        var percent = AnimSheet.Parse(FilterOf("crop=384:360:0:0,pad=384:384:0:0:color=%s"));
        Check(!percent.Ok, "参数里出现 % 之类的特殊字符会被拒：" + percent.Error);

        var tooLong = AnimSheet.Parse(FilterOf("crop=" + new string('9', 600) + ":1:0:0"));
        Check(!tooLong.Ok && tooLong.Error.Contains("太长", StringComparison.Ordinal),
            "过长的滤镜串直接拒绝");

        var spacePath = AnimSheet.Parse("""
        {"type":"video","video":"..\\..\\evil.webm","filter":"crop=1:1:0:0","frameWidth":384,"frameHeight":384,"fps":24}
        """);
        Check(!spacePath.Ok, "视频文件名不许带路径（不能指到素材目录外面）：" + spacePath.Error);

        var sequence = AnimSheet.Parse("""{"type":"sequence","video":"v","frameWidth":1,"frameHeight":1,"fps":24}""");
        Check(!sequence.Ok && sequence.Error.Contains("video", StringComparison.Ordinal),
            "不支持的 type 要顺便说清该写什么：" + sequence.Error);

        // ------------------------------------------------------------------
        Section("动态素材：视频动作的目录扫描与角色");

        var assetDir = Path.Combine(workspace, "video-assets");
        var animDir = Path.Combine(assetDir, AnimLibrary.FolderName);

        WriteVideoAction(animDir, "idle-待机呼吸休闲", loop: true);
        WriteVideoAction(animDir, "clickBounce-开心跃动", loop: false);
        WriteVideoAction(animDir, "clickBounce-元气挥手", loop: false);
        WriteVideoAction(animDir, "ambient-写代码", loop: false);
        WriteVideoAction(animDir, "drag-被拖着", loop: true);

        var lib = AnimLibrary.Scan(assetDir);

        CheckEq(lib.ActionNames.Count.ToString(), "5", "五个视频动作都被扫到");
        Check(lib.VideoCount == 5 && lib.SheetCount == 0,
            $"扫到的都是视频：视频 {lib.VideoCount}、精灵图 {lib.SheetCount}");
        Check(lib.Problems.Count == 0, "正常素材不该有问题：" + string.Join(" / ", lib.Problems));

        var idlePath = lib.MediaPath("idle-待机呼吸休闲");
        Check(idlePath != null && idlePath.EndsWith("video.webm", StringComparison.OrdinalIgnoreCase) &&
              Path.GetDirectoryName(idlePath)!.EndsWith("idle-待机呼吸休闲", StringComparison.Ordinal),
            $"视频的素材路径落在动作自己的目录里：{idlePath}");

        CheckEq(lib.ActionsWithRole(AnimActions.Click).Count.ToString(), "2", "点击角色下有两个动作（界面随机挑）");
        CheckEq(lib.ActionsWithRole(AnimActions.Idle).Count.ToString(), "1", "待机角色下有一个动作");
        CheckEq(lib.ActionsWithRole(AnimActions.Ambient).Count.ToString(), "1", "闲置小动作有一个");
        CheckEq(lib.RoleOf("idle-待机呼吸休闲"), AnimActions.Idle, "库里也能按动作名问出角色");
        Check(lib.RecognizedRoleCount == 5, $"五个动作都认得角色（实际 {lib.RecognizedRoleCount}）");

        Check(lib.Describe().Contains("透明视频", StringComparison.Ordinal),
            "诊断报告里说清了这些是透明视频：" + lib.Describe().Replace("\n", " | "));

        // 视频动作一旦存在，诊断就必须顺带报告 ffmpeg 找没找到 ——
        // "动画不播"十有八九是它缺失，报告里没有这一行会查很久
        Check(lib.Describe().Contains("ffmpeg", StringComparison.Ordinal),
            "有视频动作时诊断报告会报 ffmpeg 状态");

        // ------------------------------------------------------------------
        Section("动态素材：真的解一遍（ffmpeg 流式解码）");

        var ffmpeg = FfmpegLocator.Path;
        if (ffmpeg == null)
        {
            Console.WriteLine("  · 跳过：本机没找到 ffmpeg（" + FfmpegLocator.Where + "）");
        }
        else
        {
            TestVideoDecode(ffmpeg, workspace);
            TestVideoAlpha(ffmpeg, workspace);
        }
    }

    /// <summary>
    /// 起一个真的 ffmpeg，把视频解成 RGBA 帧走一遍。
    ///
    /// 这一段测的是环形缓冲和读帧线程：取帧、还帧、解完（EOF）、循环。
    /// 这几样出错的表现都很隐蔽 —— 要么卡住不动，要么跑着跑着内存涨上去。
    /// </summary>
    private static void TestVideoDecode(string ffmpeg, string workspace)
    {
        var dir = Path.Combine(workspace, "video-decode");
        Directory.CreateDirectory(dir);

        const int side = 64;
        const int sourceFrames = 24;
        var clip = Path.Combine(dir, "clip.avi");

        // 造一段 24 帧的 64×64 素材：用 ffmpeg 自己生成，不依赖仓库里带一个视频文件
        var made = RunFfmpeg(ffmpeg, new[]
        {
            "-v", "error",
            "-f", "lavfi", "-i", $"testsrc=size={side}x{side}:rate=24",
            "-frames:v", sourceFrames.ToString(),
            "-c:v", "mpeg4", "-y", clip,
        });

        if (!File.Exists(clip))
        {
            Check(false, "造不出测试视频，跳过解码测试：" + made);
            return;
        }

        // ---- 一次性（非循环）----
        try
        {
            using var stream = new VideoFrameStream(ffmpeg, clip, filter: "", frameWidth: side, frameHeight: side, loop: false);

            Check(stream.FrameBytes == side * side * 4,
                $"一帧的字节数按格子算：{stream.FrameBytes}");

            int taken = 0;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (!stream.TryTake(out var frame, 1500))
                {
                    if (stream.IsEof) break;
                    continue;
                }

                if (frame == null || frame.Length != side * side * 4)
                {
                    Check(false, "取到的帧大小不对");
                    stream.Return(frame);
                    break;
                }

                taken++;
                stream.Return(frame);

                // 非循环素材：解到 EOF 之后就该停，不能没完没了
                if (taken > sourceFrames + 8) break;
            }

            Check(taken >= sourceFrames - 2 && taken <= sourceFrames + 2,
                $"非循环视频解出 {taken} 帧（源素材 {sourceFrames} 帧）");
            Check(stream.IsEof && stream.IsDrained,
                "非循环视频解完之后明确进入「解完」状态（界面靠它判断这一段播完了）");
            Check(stream.Error.Length == 0, "解码过程没有报错：" + stream.Error);
        }
        catch (Exception ex)
        {
            Check(false, "非循环视频解码抛异常：" + ex.Message);
        }

        // ---- 循环（待机、拖拽这类要一直播的）----
        try
        {
            using var stream = new VideoFrameStream(ffmpeg, clip, filter: "", frameWidth: side, frameHeight: side, loop: true);

            // 循环素材必须能解出比源帧数更多的帧 —— 解到源帧数就 EOF 的话，
            // 待机动画每 10 秒会停一次，而那种"偶尔卡一下"极难归因
            int taken = 0;
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (taken < sourceFrames + 10 && DateTime.UtcNow < deadline)
            {
                if (stream.TryTake(out var frame, 1500)) { taken++; stream.Return(frame); }
                else if (stream.IsEof) break;
            }

            Check(taken >= sourceFrames + 10,
                $"循环视频能一直解下去：解了 {taken} 帧（源素材只有 {sourceFrames} 帧）");
            Check(!stream.IsEof, "循环中的视频不该报「解完」");
        }
        catch (Exception ex)
        {
            Check(false, "循环视频解码抛异常：" + ex.Message);
        }

        // ---- 参数不合法要立刻拒绝，而不是起一个必死的进程 ----
        try
        {
            using var bad = new VideoFrameStream(ffmpeg, clip, "",
                frameWidth: 4, frameHeight: 4, loop: false);
            Check(false, "画布边长过小应该被拒（4px）");
        }
        catch (ArgumentOutOfRangeException)
        {
            Check(true, "画布边长过小会当场被拒（不会去起一个必死的进程）");
        }
        catch (Exception ex)
        {
            Check(false, "画布边长过小时抛的不是预期异常：" + ex.GetType().Name);
        }

        // ---- 非方形画布（2026-09-21 新增的能力）----
        //
        // 以前这一类只支持方形：帧缓冲、每帧字节数、贴图行距全按"单边"算。
        // dsh 那套源素材是 640×360，为了塞进方形格子只能先 crop 成方的 ——
        // 106 个动作里 25 个的道具被切成平口，用户看到的就是"新形象显示被裁"。
        //
        // 所以这条断言验的是"非方形真的能解"：字节数按 宽×高×4，
        // 而且解出来的帧长就是这个数（错的话 TryTake 一辈子取不到帧）。
        try
        {
            int w, h = side / 2;
            w = side;
            using var wide = new VideoFrameStream(ffmpeg, clip,
                filter: $"crop={w}:{h}:0:0",
                frameWidth: w, frameHeight: h, loop: false);

            Check(wide.FrameBytes == w * h * 4,
                $"非方形画布的一帧字节数 = 宽×高×4：{wide.FrameBytes}（{w}×{h}×4）");

            var got = wide.TryTake(out var frame, 10000) && frame != null && frame.Length == w * h * 4;
            Check(got, got ? $"非方形画布解出了一帧（{w}×{h}）"
                           : $"非方形画布解不出帧：{wide.Error}");
        }
        catch (Exception ex)
        {
            Check(false, "非方形画布解码抛异常：" + ex.Message);
        }
    }

    /// <summary>
    /// 透明素材解出来的像素**内容**必须是对的：alpha 在、红蓝没反。
    ///
    /// 这两段都是补的账，而且是同一类错：
    ///   · alpha —— 形象动画一直顶着黑底，因为 ffmpeg 默认挑的 native vp9 解码器
    ///     会把 VP9 的 alpha（存在「附加数据」里）整条丢掉，解出来照样一帧帧像素、
    ///     帧数全对、一个错都不报，只有 alpha 位全成了 255；
    ///   · 通道序 —— 解码器吐的是 rgba，而界面按 Bgra32（内存序 B,G,R,A）贴图，
    ///     红蓝整个对调，深蓝紫头发显示成栗棕红。
    ///
    /// 两次的测试都只数了帧数和字节数，所以两次都一路绿灯发了出去。
    /// 现在改成直接验像素：透明值有多少、红值落在哪条通道。
    /// </summary>
    private static void TestVideoAlpha(string ffmpeg, string workspace)
    {
        Section("透明视频：alpha 与通道顺序");

        var dir = Path.Combine(workspace, "video-alpha");
        Directory.CreateDirectory(dir);

        const int side = 64;
        var clip = Path.Combine(dir, "alpha.webm");

        // 左半边不透明的红、右半边全透明
        var made = RunFfmpeg(ffmpeg, new[]
        {
            "-v", "error",
            "-f", "lavfi",
            "-i", $"nullsrc=s={side}x{side}:r=24,format=rgba," +
                  $"geq=r='255':g='0':b='0':a='if(lt(X,{side / 2}),255,0)'",
            "-frames:v", "24",
            "-c:v", "libvpx-vp9",
            "-pix_fmt", "yuva420p",
            "-y", clip,
        });

        if (!File.Exists(clip))
        {
            Check(false, "造不出带 alpha 的测试素材，跳过：" + made);
            return;
        }

        Check(VideoFrameStream.IsVp9Webm(clip),
            "能认出这是 VP9 素材（webm 的 CodecID 是明文，不用起进程）");
        Check(!VideoFrameStream.IsVp9Webm(Path.Combine(dir, "根本没有这个文件.webm")),
            "文件不存在时当成「不是 VP9」，而不是崩掉");
        Check(VideoFrameStream.FfmpegSupportsVp9Alpha(ffmpeg),
            "这台 ffmpeg 认 libvpx-vp9 解码器");

        try
        {
            using var stream = new VideoFrameStream(ffmpeg, clip, filter: "", frameWidth: side, frameHeight: side, loop: false);

            Check(stream.UsingLibvpx, "透明素材点名了 libvpx 解码器");
            Check(stream.HasAlpha, "素材被判定为带透明");

            if (!stream.TryTake(out var frame, 10000) || frame == null)
            {
                Check(false, "取不到帧：" + stream.Error);
                return;
            }

            int clear = 0, opaque = 0;
            for (int i = 3; i < frame.Length; i += 4)
            {
                if (frame[i] == 0) clear++;
                else if (frame[i] == 255) opaque++;
            }

            int total = frame.Length / 4;

            // 关键的两条：native vp9 解出来的帧在这两条上都会挂（透明像素是 0 个）
            Check(clear > 0, $"解出来的帧有透明像素：{clear}/{total}");
            Check(opaque > 0, $"解出来的帧有实心像素：{opaque}/{total}");

            // 左半不透明、右半透明。alpha 在 VP9 里是 4:2:0 子采样的，
            // 两个半区交界会有一两个像素的过渡，所以各自往里让 2 像素再看。
            int leftOpaque = 0, leftTotal = 0, rightClear = 0, rightTotal = 0;
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side / 2 - 2; x++)
                {
                    leftTotal++;
                    if (frame[(y * side + x) * 4 + 3] > 200) leftOpaque++;
                }

                for (int x = side / 2 + 2; x < side; x++)
                {
                    rightTotal++;
                    if (frame[(y * side + x) * 4 + 3] < 60) rightClear++;
                }
            }

            Check(leftOpaque == leftTotal, $"左半边确实是不透明的：{leftOpaque}/{leftTotal}");
            Check(rightClear == rightTotal, $"右半边确实是透明的：{rightClear}/{rightTotal}");

            // 通道顺序：左半边是**纯红**，红值必须落在 R 通道上。
            //
            // 这一条是补第二次账：解码器以前吐 rgba，界面那边按 Bgra32 解读，
            // 红蓝整个对调 —— 深蓝紫的头发显示成栗棕红，用户的原话是"像反色了"。
            // 而上面那几条（有没有透明像素、左右半边对不对、帧数对不对）在互换时
            // **全部通过**，所以这个错连着发了三个版本。
            Check(VideoFrameStream.PixelFormat == "bgra",
                $"解码输出格式是 bgra（当前 {VideoFrameStream.PixelFormat}）");

            long sumB = 0, sumG = 0, sumR = 0;
            int sampled = 0;
            for (int y = 0; y < side; y++)
            {
                for (int x = 6; x < side / 2 - 6; x++)
                {
                    int i = (y * side + x) * 4;
                    if (frame[i + VideoFrameStream.OffsetA] != 255) continue;
                    sumB += frame[i + VideoFrameStream.OffsetB];
                    sumG += frame[i + VideoFrameStream.OffsetG];
                    sumR += frame[i + VideoFrameStream.OffsetR];
                    sampled++;
                }
            }

            if (sampled == 0)
            {
                Check(false, "左半采不到实心像素来验通道顺序");
            }
            else
            {
                int avgB = (int)(sumB / sampled);
                int avgG = (int)(sumG / sampled);
                int avgR = (int)(sumR / sampled);
                Check(avgR > avgB + 60 && avgR > avgG + 60,
                    $"纯红素材解出来还是红的（R={avgR} G={avgG} B={avgB}，红蓝没互换）");
            }
        }
        catch (Exception ex)
        {
            Check(false, "透明素材解码抛异常：" + ex.Message);
        }
    }

    /// <summary>造一个视频动作目录（Scan 只验文件在不在，内容不重要）。</summary>
    private static void WriteVideoAction(string animDir, string name, bool loop)
    {
        var dir = Path.Combine(animDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "video.webm"), new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });

        File.WriteAllText(Path.Combine(dir, "anim.json"), $$"""
        {
          "name": "{{name}}",
          "type": "video",
          "video": "video.webm",
          "filter": "crop=384:360:128:0,pad=384:384:0:6:color=black@0,crop=384:384:0:0",
          "frameWidth": 384, "frameHeight": 384,
          "cols": 1, "rows": 241, "count": 241, "fps": 24.0,
          "loop": {{(loop ? "true" : "false")}},
          "characterHeight": 270,
          "anchor": { "x": 192.0, "y": 336.0 }
        }
        """);
    }

    /// <summary>跑一次 ffmpeg，返回 stderr 的最后几行（用来报错）。</summary>
    private static string RunFfmpeg(string exe, string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return "进程没起来";

            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(20000);
            return err.Length > 400 ? err[^400..] : err;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ==================================================================
    // 闲置动作：按工作状态挑选
    // ==================================================================

    /// <summary>
    /// 桌面小鲸鱼原先从 93 个闲置动作里等概率抽，现在按"今天干得怎么样"加权。
    ///
    /// 这里验判定与算术那半边（纯逻辑）；界面侧只负责把事实读成 Context、把挑出来的播了。
    /// 随机那部分用固定种子的统计来验 —— "权重确实把分布掰弯了"这件事，
    /// 只能靠跑几千次看比例，断言具体某一次挑中什么没有意义。
    /// </summary>
    private static void TestAmbientPicker()
    {
        var afternoon = new DateTime(2026, 10, 8, 15, 0, 0);

        // ---------------- 状态判定 ----------------
        Section("闲置动作：状态判定");

        var blank = new AmbientPicker.Context(afternoon, 0, 0, 0, 18, null);
        var blankStates = AmbientPicker.StatesOf(blank);
        Check(blankStates.Contains(AmbientPicker.NotStarted), "一个字的下午 → 还没开工");
        Check(blankStates.Contains(AmbientPicker.PurseEmpty), "没记录也没文件 → 今天的钱包是空的");
        Check(!blankStates.Contains(AmbientPicker.AllDone), "空白不等于「都收尾了」");

        var busy = new AmbientPicker.Context(afternoon, 4, 1, 0, 18, afternoon.AddMinutes(-20));
        Check(AmbientPicker.StatesOf(busy).Contains(AmbientPicker.InProgress), "4 条记录、1 条没收尾 → 正在推进");
        Check(!AmbientPicker.StatesOf(busy).Contains(AmbientPicker.Backlog), "只堆了 1 条不算「堆着」");
        Check(!AmbientPicker.StatesOf(busy).Contains(AmbientPicker.Stale), "20 分钟前刚动过，不算久没动");

        var piled = new AmbientPicker.Context(afternoon, 6, 3, 0, 18, afternoon.AddMinutes(-10));
        Check(AmbientPicker.StatesOf(piled).Contains(AmbientPicker.Backlog), "3 条没收尾 → 堆着");
        Check(AmbientPicker.StatesOf(piled).Contains(AmbientPicker.PurseRich), "6 条记录 → 今天的钱包是满的");

        var done = new AmbientPicker.Context(afternoon, 5, 0, 0, 18, afternoon.AddMinutes(-5));
        var doneStates = AmbientPicker.StatesOf(done);
        Check(doneStates.Contains(AmbientPicker.AllDone), "都标完了 → 今天收尾了");
        Check(!doneStates.Contains(AmbientPicker.Backlog), "收尾了不该还算「堆着」");

        var night = new AmbientPicker.Context(new DateTime(2026, 10, 8, 2, 0, 0), 0, 0, 0, 18, null);
        Check(AmbientPicker.StatesOf(night).Contains(AmbientPicker.LateNight), "凌晨两点 → 深夜");
        Check(!AmbientPicker.StatesOf(night).Contains(AmbientPicker.NotStarted),
            "凌晨没记录不叫「还没开工」—— 这个点本来就该没记录");

        Check(AmbientPicker.StatesOf(new AmbientPicker.Context(new DateTime(2026, 10, 8, 7, 0, 0), 0, 0, 0, 18, null))
                .Contains(AmbientPicker.EarlyMorning), "早上七点 → 清晨");

        var evening = new AmbientPicker.Context(new DateTime(2026, 10, 8, 19, 30, 0), 3, 0, 1, 18,
                                                new DateTime(2026, 10, 8, 18, 0, 0));
        Check(AmbientPicker.StatesOf(evening).Contains(AmbientPicker.AfterHours), "过了下班点 → 下班后");

        var staleCtx = new AmbientPicker.Context(afternoon, 3, 1, 0, 18, afternoon.AddHours(-3));
        Check(AmbientPicker.StatesOf(staleCtx).Contains(AmbientPicker.Stale), "最后一次动记录是 3 小时前 → 久没动");

        // ---------------- 权重 ----------------
        Section("闲置动作：只有状态相关的动作被抬权");

        var pool = new[]
        {
            "ambient-工作状态-垂头叹气冒汗",   // 堆着的时候该演这个
            "ambient-吃冰淇淋融化",           // 这个状态下不该演
        };
        var weights = AmbientPicker.Weigh(pool, piled);
        Check(Math.Abs(weights["ambient-工作状态-垂头叹气冒汗"] - AmbientPicker.PreferredWeight) < 0.001,
            $"堆着没收尾时「垂头叹气冒汗」抬到 {AmbientPicker.PreferredWeight} 倍");
        Check(Math.Abs(weights["ambient-吃冰淇淋融化"] - 1.0) < 0.001,
            "无关动作的权重还是 1 —— 不会被清出池子，只是不那么常出现");
        Check(weights.Count == pool.Length, "权重表覆盖传进来的每一个动作（调用方不用再补默认值）");

        // ---------------- 加权随机（统计） ----------------
        Section("闲置动作：加权随机确实把分布掰弯了");

        var rng = new Random(20261008);
        const int trials = 4000;
        var mixed = new[]
        {
            "ambient-工作状态-垂头叹气冒汗", "ambient-工作状态-忙碌点按", "ambient-吃Token",
            "ambient-吃冰淇淋融化", "ambient-荡秋千", "ambient-撸猫", "ambient-吹笛子",
            "ambient-放风筝", "ambient-堆雪人", "ambient-抽陀螺",
        };

        int preferredHits = 0;
        for (int i = 0; i < trials; i++)
        {
            var pick = AmbientPicker.Pick(mixed, piled, Array.Empty<string>(), rng);
            if (pick is "ambient-工作状态-垂头叹气冒汗" or "ambient-工作状态-忙碌点按" or "ambient-吃Token")
                preferredHits++;
        }
        var rate = preferredHits / (double)trials;
        // 理论：3×5 / (3×5 + 7×1) = 15/22 ≈ 68%；等概率的话只有 30%
        Check(rate > 0.55 && rate < 0.80,
            $"状态相关的 3 个占了 {rate:P0}（理论约 68%，等概率只有 30%）");

        // ---------------- 刚播过的降权 ----------------
        Section("闲置动作：刚播过的降权");

        var two = new[] { "ambient-工作状态-忙碌点按", "ambient-堆雪人" };

        int hot = 0;
        for (int i = 0; i < trials; i++)
            if (AmbientPicker.Pick(two, piled, Array.Empty<string>(), rng) == "ambient-工作状态-忙碌点按") hot++;
        var hotRate = hot / (double)trials;
        Check(hotRate > 0.75, $"没播过时那个动作占 {hotRate:P0}（5/6 ≈ 83%）");

        int cooled = 0;
        var recent = new[] { "ambient-工作状态-忙碌点按" };
        for (int i = 0; i < trials; i++)
            if (AmbientPicker.Pick(two, piled, recent, rng) == "ambient-工作状态-忙碌点按") cooled++;
        var cooledRate = cooled / (double)trials;
        Check(cooledRate < 0.5,
            $"刚播过之后掉到 {cooledRate:P0}（0.75/1.75 ≈ 43%）——「刚叹完气又叹气」被压住了");

        // ---------------- 边界 ----------------
        Section("闲置动作：边界");

        Check(AmbientPicker.Pick(Array.Empty<string>(), piled, Array.Empty<string>(), rng) == null,
            "素材池为空 → 返回 null（调用方跳过，而不是硬编码假设素材一定在）");

        var unknown = new[] { "ambient-用户自己画的" };
        Check(Math.Abs(AmbientPicker.Weigh(unknown, piled)["ambient-用户自己画的"] - 1.0) < 0.001,
            "认不出来的自制动作权重保持 1 —— 自己做的素材不会因为「不在名单里」就播不到");

        // 同一动作被两个状态同时命中时取最大、不连乘：
        // 3 条记录全收尾 + 最后活动在 3 小时前 → allDone 和 stale 都含「摇扇纳凉」
        var dualCtx = new AmbientPicker.Context(afternoon, 3, 0, 0, 18, afternoon.AddHours(-3));
        var dualStates = AmbientPicker.StatesOf(dualCtx);
        Check(dualStates.Contains(AmbientPicker.AllDone) && dualStates.Contains(AmbientPicker.Stale),
            "构造得对：这个场景同时命中「都收尾了」和「久没动」");
        var dual = AmbientPicker.Weigh(new[] { "ambient-摇扇纳凉" }, dualCtx);
        Check(Math.Abs(dual["ambient-摇扇纳凉"] - AmbientPicker.PreferredWeight) < 0.001,
            $"同时命中两个状态也还是 {AmbientPicker.PreferredWeight} 倍，不是 5×5 —— 连乘会把池子挤成确定性的几个");

        // 加了一个新状态却忘了给它配偏好动作，表现是"这个状态下跟以前一样纯随机"——
        // 不报错、看不出来，所以在这儿拦一道
        var allStates = new[]
        {
            AmbientPicker.LateNight, AmbientPicker.EarlyMorning, AmbientPicker.NotStarted,
            AmbientPicker.InProgress, AmbientPicker.Backlog, AmbientPicker.AllDone,
            AmbientPicker.Stale, AmbientPicker.AfterHours,
            AmbientPicker.PurseRich, AmbientPicker.PurseMid, AmbientPicker.PurseEmpty,
        };
        var noHints = allStates.Where(s => AmbientPicker.HintsOf(s).Count == 0).ToList();
        Check(noHints.Count == 0, "每个状态都配了偏好动作"
            + (noHints.Count > 0 ? "，缺：" + string.Join("、", noHints) : ""));

        Check(AmbientPicker.AllHints.Count >= 40, $"hint 足够细：{AmbientPicker.AllHints.Count} 条");
    }

    // ==================================================================
    // 形象包
    // ==================================================================

    /// <summary>
    /// 形象包：新版溟月与原版鲸鱼娘并存、能一键切换，而且**永远解析得到一个能用的形象**。
    /// </summary>
    private static void TestCharacterPacks(string workspace)
    {
        Section("形象包：发现与降级");

        var root = Path.Combine(workspace, "pack-assets");

        Check(CharacterPacks.IsAvailable(CharacterPacks.ClassicId, root),
            "原版形象永远可用（它的图是编译进程序集的嵌入资源）");
        Check(!CharacterPacks.IsAvailable(CharacterPacks.DshId, root),
            "角色目录还不存在时说它不可用（界面据此把它标灰）");

        CheckEq(CharacterPacks.Resolve("dsh", root), CharacterPacks.ClassicId,
            "设置里要 dsh 但素材不在 → 降级到原版，而不是解析出一个空形象");
        CheckEq(CharacterPacks.Resolve("从旧版本升上来的未知ID", root), CharacterPacks.ClassicId,
            "配置里是个认不出来的 ID 也降级到原版");
        CheckEq(CharacterPacks.DirOf(CharacterPacks.ClassicId, root), root,
            "原版不额外套一层目录（老用户放在 Assets\\ 根下的图不能失效）");

        // 把新形象摆上
        var dshDir = Path.Combine(root, CharacterPacks.FolderName, CharacterPacks.DshId);
        Directory.CreateDirectory(Path.Combine(dshDir, "anim"));
        File.WriteAllBytes(Path.Combine(dshDir, "whale.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        WriteVideoAction(Path.Combine(dshDir, "anim"), "idle-待机呼吸休闲", loop: true);

        Check(CharacterPacks.IsAvailable(CharacterPacks.DshId, root), "有 whale.png 就算可用");
        CheckEq(CharacterPacks.Resolve("", root), CharacterPacks.DshId,
            "没选过（空串）时默认用新形象 —— 装了新素材就该看到新的");
        CheckEq(CharacterPacks.Resolve(CharacterPacks.DshId, root), CharacterPacks.DshId, "选了的能用就用选的");
        CheckEq(CharacterPacks.Resolve(CharacterPacks.ClassicId, root), CharacterPacks.ClassicId,
            "选回原版就真的是原版（切换开关是双向的）");

        var dshRoot = CharacterPacks.DirOf(CharacterPacks.DshId, root);
        Check(dshRoot.EndsWith(Path.Combine(CharacterPacks.FolderName, CharacterPacks.DshId), StringComparison.Ordinal),
            $"新形象有自己的素材根：{dshRoot}");

        // 两个形象各扫各的：新形象的动作不该被原版的看到，反之亦然
        var dshLib = AnimLibrary.Scan(dshRoot);
        Check(dshLib.HasAny && dshLib.ActionNames.Count == 1,
            $"新形象扫到自己的动作：{string.Join("、", dshLib.ActionNames)}");

        var classicLib = AnimLibrary.Scan(root);
        Check(classicLib.IsEmpty || !classicLib.ActionNames.Contains("idle-待机呼吸休闲"),
            "原版的扫描不会串到 characters\\ 底下去（否则两个形象的动作会混在一起）");

        Check(CharacterPacks.TitleOf(CharacterPacks.DshId).Length > 0 &&
              !CharacterPacks.TitleOf(CharacterPacks.DshId).Equals(CharacterPacks.DshId, StringComparison.Ordinal),
            "新形象在界面上有正经名字：" + CharacterPacks.TitleOf(CharacterPacks.DshId));

        // ID 里被塞了路径分隔符时不能越出素材目录 —— 这个值来自可手改的配置文件
        CheckEq(CharacterPacks.DirOf("..\\..\\Windows", root), root,
            "ID 带路径分隔符 → 退回素材根（不能让配置文件指到系统目录去）");

        // 用户自己丢一个形象包进去也该被发现（不用改代码）
        Directory.CreateDirectory(Path.Combine(root, CharacterPacks.FolderName, "自制形象"));
        Check(CharacterPacks.Discover(root).Contains("自制形象"),
            "用户自制形象包能被发现：" + string.Join("、", CharacterPacks.Discover(root)));
        Check(CharacterPacks.Available(root).Any(p => p.Id == "自制形象" && !p.IsAvailable),
            "自制形象没放图时标记为不可用（界面上标灰而不是让你选了没反应）");

        var packs = CharacterPacks.Available(root);
        Check(packs.Count >= 3 && packs[0].Id == CharacterPacks.DshId,
            "内置形象排在最前、新版优先：" + string.Join("、", packs.Select(p => p.Id)));
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
