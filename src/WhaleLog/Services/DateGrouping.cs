using WhaleLog.Models;

namespace WhaleLog.Services;

/// <summary>
/// 「全部」视图里按归档日期分组用的排序与栏头文案。
///
/// 刻意放在界面代码之外：日期从新到旧、同一天内按拖入时刻、没有日期的记录排到哪、
/// 跨年要不要带年份 —— 这些都是"看着对、错一位也不显眼"的地方，需要能被自测直接断言。
/// </summary>
public static class DateGrouping
{
    /// <summary>缺日期的记录用什么分组键。空串在倒序里天然排到最后。</summary>
    public const string UnknownKey = "";

    /// <summary>
    /// 按归档日期从新到旧排列，同一天内按拖入时刻从新到旧。
    ///
    /// 日期键是 yyyy-MM-dd，定长零填充，所以字符串倒序就是时间倒序。
    /// 最后用文件名兜一次，保证同一时刻拖入的多个文件顺序稳定（否则快照式的断言会随机失败）。
    /// </summary>
    public static List<FileRecord> Sort(IEnumerable<FileRecord> files)
        => files
            .OrderByDescending(
                f => string.IsNullOrEmpty(f.AddedDate) ? UnknownKey : f.AddedDate,
                StringComparer.Ordinal)
            .ThenByDescending(f => f.AddedAt)
            .ThenBy(f => f.FileName, StringComparer.Ordinal)
            .ToList();

    /// <summary>某个日期键上的分组数量（同一日期算一组，没有日期的单独一组）。</summary>
    public static int CountGroups(IEnumerable<FileRecord> files)
        => files
            .Select(f => string.IsNullOrEmpty(f.AddedDate) ? UnknownKey : f.AddedDate)
            .Distinct(StringComparer.Ordinal)
            .Count();

    /// <summary>日期栏上的文案，例如「今天 · 9月11日」「周一 · 9月7日」。</summary>
    public static string Label(string? dateKey) => Label(dateKey, DateTime.Today);

    /// <summary>带参考日期的重载，便于自测把"今天"钉死。</summary>
    public static string Label(string? dateKey, DateTime today)
    {
        if (!DateTime.TryParse(dateKey, out var d))
            return "未标注日期";

        var head = d.Date == today.Date ? "今天"
                 : d.Date == today.Date.AddDays(-1) ? "昨天"
                 : ReportService.WeekdayText(d);

        // 当年的日期不重复写年份，跨年的必须写，否则"9月7日"到底哪一年会看不出来
        var text = d.Year == today.Year ? $"{d:M月d日}" : $"{d:yyyy年M月d日}";
        return $"{head} · {text}";
    }
}
