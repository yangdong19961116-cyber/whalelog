using WhaleLog.Models;

namespace WhaleLog.Services;

/// <summary>
/// 某一天的内容摘要：几条记录、几条没收尾、几个归档文件。
///
/// 存在的理由是"回看往期"这个动作需要**先知道哪天有东西**，才知道往哪跳。
/// 以前这件事只有月历上的小圆点在表达，而圆点只能告诉你"有"，说不出"有多少"，
/// 也没法点着列表往下看。摘要里的条数就是清单上那行字的来源。
/// </summary>
public sealed class DayDigest
{
    /// <summary>日期键（yyyy-MM-dd）。</summary>
    public string Date { get; init; } = "";

    /// <summary>当天的手写记录条数。</summary>
    public int Entries { get; init; }

    /// <summary>其中还没收尾的条数。</summary>
    public int Open { get; init; }

    /// <summary>当天归档的文件数。</summary>
    public int Files { get; init; }

    /// <summary>记录原文。搜索往期内容时用，也用来给出"命中在哪一条"。</summary>
    public IReadOnlyList<string> EntryTexts { get; init; } = Array.Empty<string>();

    /// <summary>归档文件名。同上。</summary>
    public IReadOnlyList<string> FileNames { get; init; } = Array.Empty<string>();

    public int Total => Entries + Files;

    /// <summary>这天算不算"有记录"。往期清单只收这样的日子。</summary>
    public bool HasContent => Total > 0;

    public bool HasOpen => Open > 0;
}

/// <summary>
/// 「哪天有记录」的索引。
///
/// 这里是**唯一**的判定处。月历上的小圆点、往期清单里的行、Alt+← / → 的落点、
/// 都从这一个 Build 的结果推导 —— 抄第二份的话，改了规则就会出现
/// "清单里有这天、月历上没有"这种最误导人的局面。
///
/// 纯逻辑、不碰界面，所以自测能直接断言（包括"没有日期的归档记录不进索引"这类边界）。
/// </summary>
public static class DayIndex
{
    /// <summary>
    /// 把整库汇总成"有内容的日子"，从新到旧。
    ///
    /// 日期键是 yyyy-MM-dd，定长零填充，所以字符串倒序就是时间倒序 ——
    /// 不需要 ToList 之后再用 DateTime 排序（跨年也不会错）。
    /// </summary>
    public static List<DayDigest> Build(IEnumerable<DayNote>? days, IEnumerable<FileRecord>? files)
    {
        var entries = new Dictionary<string, int>(StringComparer.Ordinal);
        var open = new Dictionary<string, int>(StringComparer.Ordinal);
        var entryTexts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var fileCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var fileNames = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var day in days ?? Enumerable.Empty<DayNote>())
        {
            if (string.IsNullOrEmpty(day.Date)) continue;

            foreach (var entry in day.Entries)
            {
                Bump(entries, day.Date);
                if (!entry.Done) Bump(open, day.Date);

                // 正文为空的记录（只打了标签）也要能被搜到，否则搜标签名会漏
                var text = !string.IsNullOrWhiteSpace(entry.Text) ? entry.Text : entry.Tag;
                if (!string.IsNullOrWhiteSpace(text)) Append(entryTexts, day.Date, text);
            }
        }

        foreach (var file in files ?? Enumerable.Empty<FileRecord>())
        {
            // 没有归属日期的记录不进索引：它在月历上也没有落点。
            // 「哪天有记录」必须和月历小圆点用同一套判据（见 MainWindow.CollectMarkedDates）。
            if (string.IsNullOrEmpty(file.AddedDate)) continue;

            Bump(fileCounts, file.AddedDate);
            if (!string.IsNullOrWhiteSpace(file.FileName)) Append(fileNames, file.AddedDate, file.FileName);
        }

        var keys = new HashSet<string>(entries.Keys, StringComparer.Ordinal);
        keys.UnionWith(fileCounts.Keys);

        return keys
            .Select(key => new DayDigest
            {
                Date = key,
                Entries = Get(entries, key),
                Open = Get(open, key),
                Files = Get(fileCounts, key),
                EntryTexts = entryTexts.TryGetValue(key, out var t) ? t : new List<string>(),
                FileNames = fileNames.TryGetValue(key, out var n) ? n : new List<string>(),
            })
            .Where(d => d.HasContent)
            .OrderByDescending(d => d.Date, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>这一天里有没有包含关键字的内容。空关键字（或纯空白）算全部命中。</summary>
    public static bool Matches(DayDigest day, string? keyword)
    {
        var kw = Normalize(keyword);
        if (kw.Length == 0) return true;

        return day.EntryTexts.Any(t => t.Contains(kw, StringComparison.OrdinalIgnoreCase))
            || day.FileNames.Any(n => n.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按关键字筛出有命中的日子。空关键字原样返回（顺序不变）。</summary>
    public static List<DayDigest> Search(IEnumerable<DayDigest> days, string? keyword)
    {
        var kw = Normalize(keyword);
        return kw.Length == 0
            ? days.ToList()
            : days.Where(d => Matches(d, kw)).ToList();
    }

    /// <summary>
    /// 命中在哪一条上。给清单里的"为什么这天会出现"用 —— 只过滤不说明的话，
    /// 搜出来的那天点进去还要自己找是哪条。
    /// </summary>
    public static string? FirstMatch(DayDigest day, string? keyword)
    {
        var kw = Normalize(keyword);
        if (kw.Length == 0) return null;

        return day.EntryTexts.FirstOrDefault(t => t.Contains(kw, StringComparison.OrdinalIgnoreCase))
            ?? day.FileNames.FirstOrDefault(n => n.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 从 from 出发，找到最近的另一个有内容的日子。
    /// direction &lt; 0 往更早找，&gt; 0 往更晚找；没有就返回 null（界面据此说"再往前没有了"）。
    ///
    /// 刻意不做"最多往回找 N 天"的上限：数据缺了三天和缺了三个月是同一件事，
    /// 而截断会让"我明明记过"变成"程序说没有"。
    /// </summary>
    public static DateTime? Adjacent(IEnumerable<DayDigest> days, DateTime from, int direction)
    {
        if (direction == 0) return null;

        var candidates = days
            .Select(d => DateTime.TryParse(d.Date, out var dt) ? dt.Date : (DateTime?)null)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .Where(d => direction < 0 ? d < from.Date : d > from.Date)
            .ToList();

        if (candidates.Count == 0) return null;
        return direction < 0 ? candidates.Max() : candidates.Min();
    }

    // ------------------------------------------------------------------
    // 文案
    // ------------------------------------------------------------------

    /// <summary>「9月24日」；跨年时带年份，否则"9月24日"到底是哪一年会看不出来。</summary>
    public static string DateLabel(string dateKey, DateTime today)
    {
        if (!DateTime.TryParse(dateKey, out var d)) return dateKey;
        return d.Year == today.Year ? $"{d:M月d日}" : $"{d:yyyy年M月d日}";
    }

    /// <summary>「今天」「昨天」或星期几。</summary>
    public static string WeekdayLabel(string dateKey, DateTime today)
    {
        if (!DateTime.TryParse(dateKey, out var d)) return "";

        return d.Date == today.Date ? "今天"
             : d.Date == today.Date.AddDays(-1) ? "昨天"
             : ReportService.WeekdayText(d);
    }

    /// <summary>清单里的月份分组头，例如「9月」「2025 年 9 月」。</summary>
    public static string MonthLabel(string dateKey, DateTime today)
    {
        if (!DateTime.TryParse(dateKey, out var d)) return "未标注日期";

        // 月份必须取 d.Month，不能写成 {d:M}：单个 M 在 .NET 里是**标准**格式符
        // （输出成"9月24日"这种月/日格式），不是"月份数字"，拼出来会变成「9月24日月」。
        return d.Year == today.Year ? $"{d.Month}月" : $"{d.Year} 年 {d.Month} 月";
    }

    /// <summary>
    /// 清单一行右边那截：「记录 2 · 文件 21」。
    ///
    /// 刻意用"记录 / 文件"这两个词而不是「2 条 · 21 个」—— 单看「2 条」不知道是什么的条，
    /// 而这一行右边本来就紧（左边还有「9月24日 周四」），也不适合写「2 条记录 · 21 个文件」。
    /// </summary>
    public static string CountsText(DayDigest day)
    {
        var parts = new List<string>();
        if (day.Entries > 0) parts.Add($"记录 {day.Entries}");
        if (day.Files > 0) parts.Add($"文件 {day.Files}");
        return parts.Count == 0 ? "没有内容" : string.Join(" · ", parts);
    }

    /// <summary>清单顶部那句话，例如「17 天有记录 · 16 条记录 · 44 个文件」。</summary>
    public static string SummaryText(IReadOnlyList<DayDigest> days)
    {
        if (days.Count == 0) return "还没有可回溯的记录";

        var parts = new List<string> { $"{days.Count} 天有记录" };

        int entries = days.Sum(d => d.Entries);
        int files = days.Sum(d => d.Files);
        if (entries > 0) parts.Add($"{entries} 条记录");
        if (files > 0) parts.Add($"{files} 个文件");

        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------------

    private static string Normalize(string? keyword) => (keyword ?? "").Trim();

    private static void Bump(Dictionary<string, int> map, string key)
        => map[key] = Get(map, key) + 1;

    private static void Append(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<string>();
            map[key] = list;
        }
        list.Add(value);
    }

    private static int Get(Dictionary<string, int> map, string key)
        => map.TryGetValue(key, out var value) ? value : 0;
}
