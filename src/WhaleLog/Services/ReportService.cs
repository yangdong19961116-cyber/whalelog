using System.IO;
using System.Text;
using WhaleLog.Models;

namespace WhaleLog.Services;

/// <summary>
/// 日报 / 周报自动汇总。
///
/// 汇总素材全部来自已经落盘的数据：当天的笔记条目、条目的分类与耗时、
/// 当天归档的文件和它们的版本。所以"生成日报"是一次纯读取的渲染，
/// 不会改动任何原始记录，用户随时可以重新生成一版。
/// </summary>
public static class ReportService
{
    private static readonly string[] WeekdayNames =
    {
        "周日", "周一", "周二", "周三", "周四", "周五", "周六",
    };

    /// <summary>一天的报告。</summary>
    public static string BuildDaily(LogBook book, DateTime date, bool includeFiles = true)
    {
        var day = book.FindDay(date);
        var files = book.Files
            .Where(f => f.AddedDate == date.ToString("yyyy-MM-dd"))
            .OrderBy(f => f.Category, StringComparer.Ordinal)
            .ThenBy(f => f.FileName, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# {date:yyyy-MM-dd}（{WeekdayNames[(int)date.DayOfWeek]}）工作日报");
        sb.AppendLine();

        if (day == null || (day.Entries.Count == 0 && files.Count == 0))
        {
            sb.AppendLine("_这一天没有任何记录。_");
            return sb.ToString();
        }

        var entries = day.Entries.OrderBy(e => e.CreatedAt).ToList();
        var done = entries.Where(e => e.Done).ToList();
        var open = entries.Where(e => !e.Done).ToList();

        sb.AppendLine("## 概览");
        sb.AppendLine();
        sb.AppendLine($"- 记录 **{entries.Count}** 条，完成 **{done.Count}** 条，进行中 **{open.Count}** 条");
        // 当天完成度：所有事项进度的平均（勾选记 100%）。
        // 定格过的那天用定格值 —— 日报是"回看某一天"的文档，这个数该和那天收工时一致，
        // 而不是按今天的条目重算一遍（界面上列表底部那一行是同一个数）。
        var completion = day.DisplayCompletion(DateTime.Today);
        if (completion.HasValue)
        {
            // 结过账的写清是什么时候定的格；补算出来的那种日子直说"没有记下收工时刻"，
            // 不编一个时间出来（界面上那一行的提示里是同一套说法）。
            var tail = !day.IsCompletionSettled
                ? ""
                : day.HasFrozenTime ? $"，{day.CompletionFrozenText} 收工定格" : "，没有记下收工时刻";
            sb.AppendLine($"- 当天完成度 **{completion}%**（{entries.Count} 条事项进度的平均{tail}）");
        }
        // 进行中的那几条平均做到哪儿了 —— 只看条数看不出"快收尾了"还是"刚开头"
        if (open.Count > 0) sb.AppendLine($"- 进行中平均进度 **{day.OpenProgress}%**");
        if (day.CarriedCount > 0) sb.AppendLine($"- 其中 **{day.CarriedCount}** 条由以前顺延而来");
        if (day.TotalSeconds > 0) sb.AppendLine($"- 已完成事项累计耗时 **{day.TotalDurationText}**");
        if (includeFiles && files.Count > 0) sb.AppendLine($"- 归档文件 **{files.Count}** 个");

        var byCategory = entries
            .GroupBy(e => e.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()}");
        if (entries.Count > 0) sb.AppendLine($"- 事项分布：{string.Join(" · ", byCategory)}");

        sb.AppendLine();

        AppendEntrySection(sb, "一、今日完成", done);
        AppendEntrySection(sb, "二、进行中", open);

        if (includeFiles && files.Count > 0)
        {
            sb.AppendLine("## 四、涉及文件");
            sb.AppendLine();
            foreach (var group in files.GroupBy(f => f.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var names = group.Select(f =>
                {
                    var version = f.Version > 1 ? $"（v{f.Version}）" : "";
                    var missing = f.IsMissing ? " ⚠ 已移走" : "";
                    return $"{f.FileName}{version}{missing}";
                });
                sb.AppendLine($"- **{group.Key}**（{group.Count()}）：{string.Join("、", names)}");
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(day.Summary))
        {
            var index = includeFiles && files.Count > 0 ? "五" : "四";
            sb.AppendLine($"## {index}、小结");
            sb.AppendLine();
            sb.AppendLine(day.Summary.Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendEntrySection(StringBuilder sb, string title, List<NoteEntry> entries)
    {
        if (entries.Count == 0) return;

        sb.AppendLine($"## {title}");
        sb.AppendLine();
        int index = 1;
        foreach (var e in entries)
        {
            // 元信息的口径和 notes\*.md 完全共用一份（NoteEntry.MetaParts），
            // 免得同一件事在落盘文件和日报里写法不一样。
            var meta = string.Join(" / ", e.MetaParts());
            sb.AppendLine($"{index++}. `{e.TimeText}` {e.Text}  _（{meta}）_");
        }
        sb.AppendLine();
    }

    /// <summary>一周的报告（周一为一周起点）。</summary>
    public static string BuildWeekly(LogBook book, DateTime anyDayInWeek, bool includeFiles = true)
    {
        var start = WeekStart(anyDayInWeek);
        var end = start.AddDays(6);
        var today = DateTime.Today;

        var sb = new StringBuilder();
        sb.AppendLine($"# {start:yyyy-MM-dd} ~ {end:yyyy-MM-dd} 工作周报");
        sb.AppendLine();

        var days = Enumerable.Range(0, 7)
            .Select(i => start.AddDays(i))
            .Where(d => d <= today)
            .ToList();

        var dayNotes = days
            .Select(d => (Date: d, Note: book.FindDay(d)))
            .ToList();

        var allEntries = dayNotes.SelectMany(x => x.Note?.Entries ?? new List<NoteEntry>()).ToList();
        var allFiles = book.Files
            .Where(f =>
            {
                if (!DateTime.TryParse(f.AddedDate, out var d)) return false;
                return d >= start && d <= end;
            })
            .ToList();

        if (allEntries.Count == 0 && allFiles.Count == 0)
        {
            sb.AppendLine($"_本周（{start:MM-dd} 起）还没有任何记录。_");
            return sb.ToString();
        }

        sb.AppendLine("## 概览");
        sb.AppendLine();
        sb.AppendLine($"- 有记录的天数 **{dayNotes.Count(x => x.Note is { Entries.Count: > 0 })}** / {days.Count}");
        sb.AppendLine($"- 累计记录 **{allEntries.Count}** 条，完成 **{allEntries.Count(e => e.Done)}** 条");
        // 这一周每天的完成度（过去的日子是收工定格值）取平均 —— "这周整体干得怎么样"一个数看完
        var dayCompletions = dayNotes
            .Select(x => x.Note?.DisplayCompletion(DateTime.Today))
            .Where(c => c.HasValue)
            .Select(c => (double)c!.Value)
            .ToList();
        if (dayCompletions.Count > 0)
            sb.AppendLine($"- 平均每日完成度 **{(int)Math.Round(dayCompletions.Average())}%**（{dayCompletions.Count} 天有记录）");
        var weekOpen = allEntries.Where(e => !e.Done).ToList();
        if (weekOpen.Count > 0)
            sb.AppendLine($"- 未完成 **{weekOpen.Count}** 条，平均进度 **{(int)Math.Round(weekOpen.Average(e => e.Progress))}%**");
        var totalSeconds = allEntries.Sum(e => e.DurationSeconds);
        if (totalSeconds > 0)
        {
            var span = TimeSpan.FromSeconds(totalSeconds);
            sb.AppendLine($"- 完成事项累计耗时 **{(int)span.TotalHours}h{span.Minutes:D2}m**");
        }
        if (includeFiles && allFiles.Count > 0) sb.AppendLine($"- 归档文件 **{allFiles.Count}** 个");

        var weekCategories = allEntries
            .GroupBy(e => e.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()}");
        if (allEntries.Count > 0) sb.AppendLine($"- 事项分布：{string.Join(" · ", weekCategories)}");
        sb.AppendLine();

        sb.AppendLine("## 分日明细");
        sb.AppendLine();
        foreach (var (date, note) in dayNotes)
        {
            var dayFiles = allFiles.Count(f => f.AddedDate == date.ToString("yyyy-MM-dd"));
            var dayCompletion = note?.DisplayCompletion(DateTime.Today);
            var summary = note?.Entries.Count > 0
                ? $"记录 {note.Entries.Count} 条 / 完成 {note.DoneCount} 条"
                  + (dayCompletion.HasValue ? $" / 完成度 {dayCompletion}%" : "")
                  + (dayFiles > 0 ? $" / 文件 {dayFiles} 个" : "")
                : "（无记录）";

            sb.AppendLine($"### {date:MM-dd} {WeekdayNames[(int)date.DayOfWeek]}  {summary}");
            sb.AppendLine();

            if (note?.Entries.Count > 0)
            {
                foreach (var e in note.Entries.OrderBy(x => x.CreatedAt))
                {
                    var box = e.Done ? "x" : " ";
                    // 没勾完的带上进度；顺延过来的标一下出处，不然连着几天看到同一条会以为是重复记录
                    var tail = "";
                    if (!e.Done && e.Progress > 0) tail += $"（进度 {e.Progress}%）";
                    if (e.IsCarried) tail += $"（顺延自 {e.CarriedFromText}）";
                    sb.AppendLine($"- [{box}] {e.Text}{tail}");
                }
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(note?.Summary))
            {
                sb.AppendLine($"  小结：{note!.Summary.Trim().Replace("\n", " ")}");
                sb.AppendLine();
            }
        }

        if (includeFiles && allFiles.Count > 0)
        {
            sb.AppendLine("## 涉及文件");
            sb.AppendLine();
            foreach (var group in allFiles.GroupBy(f => f.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var names = group
                    .OrderBy(f => f.FileName, StringComparer.Ordinal)
                    .Select(f => f.Version > 1 ? $"{f.FileName}（v{f.Version}）" : f.FileName);
                sb.AppendLine($"- **{group.Key}**（{group.Count()}）：{string.Join("、", names)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"---");
        sb.AppendLine($"_生成于 {DateTime.Now:yyyy-MM-dd HH:mm}_");
        return sb.ToString();
    }

    /// <summary>本周的周一。</summary>
    public static DateTime WeekStart(DateTime date)
        => date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static string WeekdayText(DateTime date) => WeekdayNames[(int)date.DayOfWeek];

    // ------------------------------------------------------------------

    public static bool TryWriteDaily(LogBook book, DateTime date, out string path, out string message)
    {
        path = "";
        try
        {
            Directory.CreateDirectory(AppPaths.ReportsDir);
            path = Path.Combine(AppPaths.ReportsDir, AppPaths.ToReportFileName(date));
            WriteAtomic(path, BuildDaily(book, date));
            message = $"日报已保存：{path}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"写入日报失败：{ex.Message}";
            return false;
        }
    }

    public static bool TryWriteWeekly(LogBook book, DateTime anyDayInWeek, out string path, out string message)
    {
        path = "";
        try
        {
            Directory.CreateDirectory(AppPaths.ReportsDir);
            path = Path.Combine(AppPaths.ReportsDir, AppPaths.ToWeeklyReportFileName(WeekStart(anyDayInWeek)));
            WriteAtomic(path, BuildWeekly(book, anyDayInWeek));
            message = $"周报已保存：{path}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"写入周报失败：{ex.Message}";
            return false;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(true));
        File.Move(tmp, path, overwrite: true);
    }
}
