using System.Globalization;
using WhaleLog.Models;

namespace WhaleLog.Services;

/// <summary>
/// 「当天完成度」的收工定格。
///
/// 用户要的是：**在次日 0 点前把当天的进度条进度存下来，记下那一天完成了百分之多少。**
/// 拆开就是两件事，各由一处保证：
///   1. 存下来 —— 每次进度条改动都会落盘（MainWindow 的 700ms 防抖 + LogBookStore.Save
///      里顺手把今天的完成度刷成实时值）。进程被杀、断电都不怕。
///   2. 记下来 —— 这一天过完的那一刻，把当时的数字写死在这天的 DayNote 上（本文件）。
///      之后翻回 10/13，看到的永远是"那天收工时的百分之多少"，而不是"按今天的条目重算一遍"。
///
/// 触发点两处，都在 App 里：启动时跑一次（补上没开机的那几天）、跨零点的轮询里跑一次
/// （程序常驻托盘，通宵开着是常态，到点就当场结账）。
///
/// 刻意跑在顺延**之前**：顺延只复制、不改源条目的进度，两边互不影响，
/// 但"先结账、再开工"这个顺序读起来才是对的。
/// </summary>
public static class CompletionService
{
    /// <summary>一次定格的结果，用于写状态栏 / 弹气泡。</summary>
    public sealed class FreezeReport
    {
        /// <summary>这次定格了几天。</summary>
        public int Days { get; init; }

        /// <summary>最近的那一天（yyyy-MM-dd）。</summary>
        public string LastDate { get; init; } = "";

        /// <summary>最近那一天定格下来的完成度。</summary>
        public int? LastCompletion { get; init; }

        public bool Any => Days > 0;

        /// <summary>给用户看的一句话；什么都没定格就是空串。</summary>
        public string Describe()
        {
            if (!Any) return "";

            var when = DateTime.TryParse(
                LastDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? $"{d:M月d日}"
                : LastDate;

            return Days == 1
                ? $"{when} 的完成度定格在 {LastCompletion}%"
                : $"已把 {Days} 天的完成度定格（最近 {when}：{LastCompletion}%）";
        }
    }

    /// <summary>
    /// 把「今天之前」的每一天里还没定过格的完成度定格掉。幂等：重复调用不会改动已定格的值。
    ///
    /// 扫的是**所有**过去的日子，不是只看昨天 —— 顺延那边踩过这个坑（只扫昨天会漏掉
    /// 几天没开机的积压），这里同理：出差一周回来，那一周每天的完成度都得补上。
    /// </summary>
    public static FreezeReport FreezePastDays(
        LogBook book, DateTime? todayOverride = null, DateTime? nowOverride = null)
    {
        var today = (todayOverride ?? DateTime.Today).Date;
        var now = nowOverride ?? DateTime.Now;
        var todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        int days = 0;
        string lastDate = "";
        int? lastValue = null;

        foreach (var day in book.Days.OrderBy(d => d.Date, StringComparer.Ordinal))
        {
            if (day.Date == todayKey) continue;

            // 日期串读不出来的（手改坏了索引）和将来的日子都跳过：
            // "未来某天"在界面上不该出现，真出现了也不能被当成已收工。
            if (!DateTime.TryParse(day.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (d.Date > today) continue;

            if (!day.Freeze(now)) continue;

            days++;
            lastDate = day.Date;
            lastValue = day.Completion;
        }

        return new FreezeReport { Days = days, LastDate = lastDate, LastCompletion = lastValue };
    }
}
