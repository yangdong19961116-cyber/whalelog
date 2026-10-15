using System.Globalization;
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
        var todayKey = Key(today);
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
    /// 单单把某一条（属于过去某天的）事项顺延到今天 —— 用户回看历史时把一条**已经完成**的
    /// 记录改回未完成，就走这条路（见 MainWindow.TrySyncUnfinishedPastEntryToToday）。
    ///
    /// 为什么不能只靠 <see cref="RunOnBook"/>：它只在**启动**和**跨零点**跑。可"昨天那条其实
    /// 没做完"这种更正，往往是第二天翻记录时才发现的，那会儿早就过了顺延的时机。不立刻补一下，
    /// 今天这边一动不动，界面上就是"我改了，可它没过来" —— 只能等下次开机才冒出来。
    ///
    /// 判定和自动顺延**完全一致**（这里是唯一的判定处，MainWindow 那边只做前置过滤）：
    ///   · 源必须是过去的日子（今天 / 将来的不搬）；
    ///   · 已完成的永不顺延；
    ///   · 已经顺延过的、或今天已经有它副本的，一律不动 —— **今天那边的进展优先**，
    ///     绝不因为回看历史就把今天已经做完的事退回未完成；
    ///   · 搬的是副本，源条目一个字不改。
    /// </summary>
    public static CarryReport CarryEntry(
        LogBook book, DateTime sourceDay, NoteEntry entry, DateTime? todayOverride = null)
    {
        var today = (todayOverride ?? DateTime.Today).Date;
        var empty = new CarryReport { Target = today };

        if (sourceDay.Date >= today) return empty;                 // 只管过去的日子
        if (entry.Done) return empty;                              // 完成的永不顺延
        if (!string.IsNullOrEmpty(entry.CarriedToDate)) return empty; // 已经搬过了

        var todayNote = book.GetOrCreateDay(today);
        if (todayNote.Entries.Any(e => e.CarriedFromId == entry.Id)) return empty; // 今天已经有副本

        todayNote.Entries.Add(entry.CloneForCarry(today));
        entry.CarriedToDate = Key(today);

        return new CarryReport
        {
            Carried = 1,
            Titles = new List<string> { entry.Text },
            Target = today,
        };
    }

    /// <summary>
    /// 顺延链上的一环：条目本身 + 它属于哪一天（yyyy-MM-dd）。
    /// 日期得跟着条目走 —— 条目自己不记得自己在哪天，只有外面那一层知道。
    /// </summary>
    public readonly record struct ChainLink(NoteEntry Entry, string Date);

    /// <summary>
    /// 一条顺延链上的全部条目：从最早那份（源头）到最新一份，含 <paramref name="entry"/> 自己。
    ///
    /// 链靠两个字段串起来，各管一头：
    ///   · 往上是 `CarriedFromId`（副本指回它的来源），只在同一条链上有效；
    ///   · 往下是 `CarriedToDate`（源条目指着自己被搬去了哪天）——
    ///     拿到那天还不行，得再在那天的条目里找"CarriedFromId == 本条.Id"的那一条。
    ///     下游只能这么两步查：条目上**没有**"我复制给了谁"的 Id，只有日期。
    ///
    /// 防环靠 visited：index.json 允许手改，两条互相指认对方的脏数据得能活着走出来，
    /// 不能死循环把界面卡住。
    /// </summary>
    public static List<ChainLink> ChainOf(LogBook book, NoteEntry entry)
    {
        var byId = new Dictionary<string, ChainLink>();
        var children = new Dictionary<(string Date, string FromId), ChainLink>();

        foreach (var day in book.Days)
        {
            foreach (var e in day.Entries)
            {
                byId[e.Id] = new ChainLink(e, day.Date);
                if (!string.IsNullOrEmpty(e.CarriedFromId))
                    children[(day.Date, e.CarriedFromId!)] = new ChainLink(e, day.Date);
            }
        }

        // ---- 一、往上追到源头 ----
        // 这一段用**自己的** visited：它只是"找头"的过程，路上经过的节点不该影响第二段。
        // 踩过：两段共用一个集合时，从中间那份出发会先把源头和它自己都标上，
        // 第二段刚走到它就当成"回到原地了"而截断 —— 一条三天链从中间看只有一天。
        var head = entry;
        var upSeen = new HashSet<string> { entry.Id };
        while (!string.IsNullOrEmpty(head.CarriedFromId)
               && byId.TryGetValue(head.CarriedFromId!, out var parent)
               && upSeen.Add(parent.Entry.Id))
        {
            head = parent.Entry;
        }

        // ---- 二、从源头沿着 CarriedToDate 一路走到最新那份 ----
        var chain = new List<ChainLink>
        {
            new(head, byId.TryGetValue(head.Id, out var headLink) ? headLink.Date : ""),
        };

        var seen = new HashSet<string> { head.Id };
        var cursor = head;
        while (!string.IsNullOrEmpty(cursor.CarriedToDate)
               && children.TryGetValue((cursor.CarriedToDate!, cursor.Id), out var child)
               && seen.Add(child.Entry.Id))
        {
            chain.Add(child);
            cursor = child.Entry;
        }

        // 走到这链条里居然没有自己（数据被人手改得前后对不上）：退回"只有我一条"，
        // 同步时就是个空操作 —— 宁可不做，也别照着半截链乱改。
        if (!seen.Contains(entry.Id))
            return new List<ChainLink> { new(entry, byId.TryGetValue(entry.Id, out var self) ? self.Date : "") };

        return chain;
    }

    /// <summary>一次编辑同步的结果，用于写状态栏。</summary>
    public sealed class SyncReport
    {
        public int Updated { get; init; }

        /// <summary>被同步到的日子（yyyy-MM-dd），按链上顺序（早→晚）。</summary>
        public List<string> Dates { get; init; } = new();

        public bool Any => Updated > 0;

        /// <summary>
        /// 给用户看的一句话；没同步就是空串。
        ///
        /// 同步会改到**别的日期**的记录，而用户此刻正盯着当前这一天 —— 界面上一点动静都没有。
        /// 所以这里必须说清楚动过哪几天，不能静默。
        ///
        /// 用「 · 」起头而不是套一对括号：这句话是接在「已修改：XXX」后面的，
        /// 正文自己常常已经带括号（"写方案（改过）"），再来一对就成了"（）（）"，读着别扭。
        /// </summary>
        public string Describe()
        {
            if (!Any) return "";

            // 最近的日期更可能被关心，倒着排；超过三天就只报天数（日期列出来太长，反而看不清）。
            var recent = Dates.AsEnumerable().Reverse().Take(3).Select(ShortDate).ToList();

            return Dates.Count > recent.Count
                ? $" · 同步到 {Dates.Count} 天的顺延记录"
                : $" · 同步到 {string.Join("、", recent)} 的顺延记录";
        }

        private static string ShortDate(string key) =>
            DateTime.TryParse(key, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? $"{d:M月d日}"
                : key;
    }

    /// <summary>
    /// 把编辑过的正文 / 分类 / 标签同步给同一条顺延链上的其他条目。
    ///
    /// 双向：编最早那份还是今天这份都一样，整条链统一成刚写下的内容。
    /// "这条任务叫这个名字"只该有一份说法，否则链条上昨天叫「写报告」、今天叫「写方案」，
    /// 看着像两件事。
    ///
    /// **只同步这三样内容字段。** 进度、勾选状态、时间、Id、顺延标记一个都不动 ——
    /// 链上各天是各自独立推进的（用户定的口径：今天那边的进展优先，
    /// 绝不因为回看历史就把今天已经做完的事退回去）。名字可以统一，进度不能。
    /// </summary>
    public static SyncReport SyncContent(LogBook book, NoteEntry edited)
    {
        var updated = 0;
        var dates = new List<string>();

        foreach (var link in ChainOf(book, edited))
        {
            var other = link.Entry;
            if (ReferenceEquals(other, edited)) continue;

            var changed = false;

            // 正文为空时**不覆盖**：编辑窗本来就拦了空正文，真从别的路进来一个空的，
            // 宁可少同步一样 ——「把一条记录的正文悄悄清空」是这里最安静的坏法。
            if (!string.IsNullOrWhiteSpace(edited.Text) && other.Text != edited.Text)
            {
                other.Text = edited.Text;
                changed = true;
            }

            if (other.Category != edited.Category)
            {
                other.Category = edited.Category;
                changed = true;
            }

            if (other.Tag != edited.Tag)
            {
                other.Tag = edited.Tag;
                changed = true;
            }

            if (!changed) continue;

            updated++;
            if (link.Date.Length > 0) dates.Add(link.Date);
        }

        return new SyncReport { Updated = updated, Dates = dates };
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

    /// <summary>
    /// yyyy-MM-dd。日期要落盘、要跨机器比，一律用不变文化 —— 跟随系统区域设置的话，
    /// 换一台机器（或换一种日历）读出来的字面量就对不上了。
    /// </summary>
    private static string Key(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
