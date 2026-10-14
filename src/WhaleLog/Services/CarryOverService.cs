using WhaleLog.Models;

namespace WhaleLog.Services;

/// <summary>
/// 未完成事项的顺延。
///
/// 规则（2026-10-14 用户定的）：
///   当天没打勾的事项，次日自动在今天的记录里出现一条**副本**，进度原样带过来。
///   原日期那条一个字都不改（只戳一个「已顺延至 X」的标记），所以翻回旧日期
///   看到的还是当天的真实情况 —— 历史完整、可以回看。
///
/// 三个必须守住的点：
///   1. **完成的永不顺延**。Done 的那一刻链条就断了。
///   2. **同一条不会被搬两次**：源条目被顺延后打上 CarriedToDate，之后一律跳过。
///      所以"今天删掉顺延来的副本"等于把这条任务丢掉，明天不会自己长回来。
///   3. 复制而不是移动：改了今天这份的进度，昨天那份还是昨天的样子。
/// </summary>
public static class CarryOverService
{
    /// <summary>一次顺延的结果，用于写状态栏 / 弹气泡。</summary>
    public sealed class CarryReport
    {
        public int Carried { get; init; }
        public List<string> Titles { get; init; } = new();
        public DateTime Target { get; init; } = DateTime.Today;

        public bool Any => Carried > 0;

        /// <summary>给用户看的一句话；没有顺延就是空串。</summary>
        public string Describe()
        {
            if (!Any) return "";
            var head = Carried == 1
                ? $"1 条未完成事项顺延到 {Target:M月d日}"
                : $"{Carried} 条未完成事项顺延到 {Target:M月d日}";
            var sample = Titles.Count == 0 ? "" : $"：{string.Join("、", Titles.Take(2))}";
            var more = Carried > 2 ? " 等" : "";
            return head + sample + more;
        }
    }

    /// <summary>
    /// 把今天之前所有「未完成且从未顺延过」的事项复制到今天，并落盘。
    /// 幂等：同一天重复调用不会产生第二份副本。
    /// </summary>
    public static CarryReport Run(LogBookStore store, DateTime? todayOverride = null)
    {
        var report = RunOnBook(store.Book, todayOverride);
        if (report.Any) store.Save();
        return report;
    }

    /// <summary>
    /// 纯逻辑版本：只动内存里的 <see cref="LogBook"/>，不碰磁盘。
    /// 自测直接调它构造各种日期组合，不用真去搭一套数据目录。
    /// </summary>
    public static CarryReport RunOnBook(LogBook book, DateTime? todayOverride = null)
    {
        var today = (todayOverride ?? DateTime.Today).Date;
        var todayKey = today.ToString("yyyy-MM-dd");
        var todayNote = book.GetOrCreateDay(today);

        // 今天已经有哪些来源的副本了（防重，和源条目的 CarriedToDate 互为双保险）
        var alreadyHere = new HashSet<string>(todayNote.Entries
            .Where(e => !string.IsNullOrEmpty(e.CarriedFromId))
            .Select(e => e.CarriedFromId!));

        int carried = 0;
        var titles = new List<string>();

        // 从早到晚遍历所有过去的日子。刻意不只看"昨天"：
        // 程序可能好几天没开机，那几天里积下来的未完成事项都要一次带到今天。
        foreach (var day in book.Days.OrderBy(d => d.Date, StringComparer.Ordinal))
        {
            if (!DateTime.TryParse(day.Date, out var dayDate)) continue;
            if (dayDate.Date >= today) continue;

            foreach (var entry in day.Entries.ToList())
            {
                if (entry.Done) continue;                                 // 完成的永不顺延
                if (!string.IsNullOrEmpty(entry.CarriedToDate)) continue;  // 已经搬过了
                if (alreadyHere.Contains(entry.Id)) continue;              // 今天已经有副本

                todayNote.Entries.Add(entry.CloneForCarry(today));

                // 源条目上戳个时间戳：既是"我已顺延"的凭证，也是翻旧日期时的交代。
                entry.CarriedToDate = todayKey;

                alreadyHere.Add(entry.Id);
                carried++;
                if (titles.Count < 3) titles.Add(entry.Text);
            }
        }

        return new CarryReport { Carried = carried, Titles = titles, Target = today };
    }

    /// <summary>
    /// 读档后把每条记录的「完成 / 进度」校准一致。
    /// 老数据的 index.json 里没有 progress 字段，不校准的话已完成的事项会显示成 0%。
    /// </summary>
    public static void Normalize(LogBook book)
    {
        foreach (var day in book.Days)
        {
            foreach (var entry in day.Entries)
                entry.Normalize();
        }
    }
}
