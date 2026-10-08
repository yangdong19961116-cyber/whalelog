using System;
using System.Collections.Generic;
using System.Linq;

namespace WhaleLog.Services;

/// <summary>
/// 按"今天干得怎么样"给闲置小动作加权 —— 让小鲸鱼从随机表演机变成工作状态的镜子。
///
/// ## 为什么需要它
///
/// dsh 那套素材有 93 个闲置小动作（<c>ambient-*</c>），里面的语义是作者分好的：
/// 「工作状态-垂头叹气冒汗」「工作状态-忙碌点按」「余额-钱袋满溢」……
/// 但界面按角色收进一个池子之后，这层语义被**拉平**了 —— 每 25~70 秒
/// <b>等概率</b>挑一个，于是「垂头叹气」和「吃冰淇淋融化」出现的概率一模一样。
/// 弯着腰忙了一下午，抬头看见小鲸鱼在荡秋千；一整天一个字没记，它却在冒汗赶工。
///
/// ## 做法：加权，不是映射
///
/// 刻意**不做**"这个状态 → 只播这几个"的确定性映射。小鲸鱼该有自己的想法，
/// 何况"用户干了什么"和"宠物该演什么"之间本来就说不准。所以：
///
///   · 所有动作的基础权重都是 1.0 —— **任何动作永远有机会出现**；
///   · 与当前状态对得上的动作，权重抬到 <see cref="PreferredWeight"/> 倍；
///   · 最近刚播过的降权（<see cref="RecentPenalty"/>），免得连着出同一个。
///
/// 净效果是"这个状态下**更可能**出这几个"，保留了随机感，但它开始跟人有关系了。
///
/// ## 分层
///
/// 这个类不碰 WPF、不碰 <c>App</c>（<see cref="Context"/> 由调用方组装），
/// 所以纯逻辑自测能直接编译它来验状态判定和权重算术。界面侧只做三件事：
/// 组装 Context、调 <see cref="Pick"/>、把挑出来的动作播了。
///
/// ## 素材改名会静默失效 —— 所以有一条断言盯着
///
/// 下面这些 hint 是**动作名的子串**，素材一改名就匹配不上，而表现只是
/// "加权不生效"（退回纯随机），界面上完全看不出异常。所以
/// <see cref="AllHints"/> 是 public 的：界面自检会拿它跟真实的素材清单核对，
/// 每条 hint 都必须至少命中一个动作，否则报错。
/// </summary>
public static class AmbientPicker
{
    // ==================================================================
    // 可调参数（都是"手感"值，改这里就够）
    // ==================================================================

    /// <summary>与当前状态对得上的动作，权重抬到多少倍。</summary>
    public const double PreferredWeight = 5.0;

    /// <summary>最近刚播过的动作，权重乘多少（1 = 不降权）。</summary>
    public const double RecentPenalty = 0.15;

    /// <summary>"最近"看多长 —— 最近这么多次里出现过的都降权。</summary>
    public const int RecentWindow = 4;

    /// <summary>今天有干活、但距最后一次活动超过这么久，算"久没动"。</summary>
    public const double StaleAfterHours = 2.0;

    /// <summary>"收获"维度：今天记录 + 归档文件到这个数算"满"。</summary>
    public const int RichYield = 5;

    // ==================================================================
    // 状态键（诊断输出与自测断言用的稳定标识，不是给人看的文案）
    // ==================================================================

    public const string LateNight = "lateNight";
    public const string EarlyMorning = "earlyMorning";
    public const string NotStarted = "notStarted";
    public const string InProgress = "inProgress";
    public const string Backlog = "backlog";
    public const string AllDone = "allDone";
    public const string Stale = "stale";
    public const string AfterHours = "afterHours";
    public const string PurseRich = "purseRich";
    public const string PurseMid = "purseMid";
    public const string PurseEmpty = "purseEmpty";

    /// <summary>状态的中文说明（诊断输出用）。</summary>
    public static string Describe(string state) => state switch
    {
        LateNight => "深夜",
        EarlyMorning => "清晨",
        NotStarted => "还没开工",
        InProgress => "正在推进",
        Backlog => "堆着没收尾",
        AllDone => "今天都收尾了",
        Stale => "久没动",
        AfterHours => "下班后",
        PurseRich => "今天收获满",
        PurseMid => "今天有进账",
        PurseEmpty => "今天颗粒无收",
        _ => state,
    };

    /// <summary>
    /// 判状态要用到的全部事实。**由调用方组装**，这个类自己去读数据的话，
    /// 就再也没法在自测里构造"下午四点、3 条记录、2 条没收尾"这种场景了。
    /// </summary>
    public readonly record struct Context(
        DateTime Now,
        int Entries,
        int OpenEntries,
        int Files,
        int WorkEndHour,
        DateTime? LastActivity)
    {
        /// <summary>今天的"产出"：手记条数 + 归档文件数。</summary>
        public int Yield => Entries + Files;
    }

    // ==================================================================
    // 状态判定
    // ==================================================================

    /// <summary>
    /// 当前命中了哪些状态（可能多个，例如"正在推进"+"久没动"）。
    ///
    /// 互斥的几组（深夜/清晨/白天、满/中/空）由条件本身保证不会同时命中；
    /// <see cref="Weigh"/> 对同一动作取各状态倍数的**最大值**而不是乘积 ——
    /// 乘积会让"既忙又久没动"变成 25 倍，把池子挤成确定性的几个，不是我们要的。
    /// </summary>
    public static IReadOnlyList<string> StatesOf(Context ctx)
    {
        var hour = ctx.Now.Hour;
        var states = new List<string>(4);

        // ---- 一天中的时段 ----
        bool lateNight = hour < 6 || hour >= 23;
        if (lateNight) states.Add(LateNight);
        else if (hour < 9) states.Add(EarlyMorning);

        // ---- 干活的状态 ----
        bool haveWork = ctx.Entries > 0 || ctx.Files > 0;

        if (ctx.OpenEntries >= 3) states.Add(Backlog);
        else if (ctx.Entries > 0 && ctx.OpenEntries > 0) states.Add(InProgress);

        if (ctx.Entries > 0 && ctx.OpenEntries == 0) states.Add(AllDone);

        // "还没开工"只在白天算：凌晨三点没记录很正常，那不该叫"还没开工"。
        if (!haveWork && !lateNight) states.Add(NotStarted);

        if (haveWork && ctx.LastActivity is { } last &&
            ctx.Now - last > TimeSpan.FromHours(StaleAfterHours))
            states.Add(Stale);

        // 下班之后（且还没到深夜）：这个点该放松，不该再演冒汗赶工
        if (!lateNight && hour >= ctx.WorkEndHour && ctx.WorkEndHour > 0)
            states.Add(AfterHours);

        // ---- 今天的收获（余额系列素材的梗）----
        if (ctx.Yield >= RichYield) states.Add(PurseRich);
        else if (ctx.Yield > 0) states.Add(PurseMid);
        else states.Add(PurseEmpty);

        return states;
    }

    // ==================================================================
    // 状态 → 偏好动作
    // ==================================================================

    /// <summary>
    /// hint 是动作名的**子串**（匹配时用 Contains，大小写不敏感）。
    ///
    /// 用子串而不是完整动作名，是为了让"ambient-工作状态-忙碌点按"这种
    /// 带前缀、带修饰的名字也能对上；但也正因为是子串，hint 要写得足够长 ——
    /// 写成「吃」会把 20 多个动作全捞进来。
    /// </summary>
    private sealed record Preference(string State, string[] Hints);

    private static readonly Preference[] Preferences =
    {
        // ---- 还没开工：闲着、张望、等着 ----
        new(NotStarted, new[]
        {
            "工作状态-原地踱步张望",
            "工作状态-思考冒泡",
            "发呆碎碎念",
            "对屏碎碎念",
            "照镜子",
            "摇扇纳凉",
            "是啊，吃什么",
        }),

        // ---- 正在推进：忙，但顺手 ----
        new(InProgress, new[]
        {
            "工作状态-忙碌点按",
            "写代码",
            "工作状态-思考冒泡",
            "轻快记录",
            "原地敲击桌面互动",
            "吃Token",
            "用鲸鱼尾巴拍打地面",
        }),

        // ---- 堆着没收尾：焦躁、硬扛 ----
        new(Backlog, new[]
        {
            "工作状态-忙碌点按",
            "工作状态-垂头叹气冒汗",
            "玩游戏气急败坏",
            "吃Token",
            "原地重力下蹲压缩",
            "深度思考碎碎念",
            "原地跳跃抓碎头顶物品",
        }),

        // ---- 今天都收尾了：放松、自己玩 ----
        new(AllDone, new[]
        {
            "悠闲哼歌",
            "轻快摇摆舞",
            "可爱宅舞",
            "撸猫",
            "吃冰淇淋融化",
            "吹笛子",
            "荡秋千",
            "摇扇纳凉",
            "小幅度原地360度旋转展示",
            "凭空生花",
            "拆礼物",
        }),

        // ---- 深夜：困 ----
        new(LateNight, new[]
        {
            "哈欠连天",
            "原地小憩沉眠",
            "打瞌睡被惊醒",
            "深度思考碎碎念",
        }),

        // ---- 清晨：刚起 ----
        new(EarlyMorning, new[]
        {
            "晨间刷牙",
            "吃早餐",
            "超大伸懒腰",
            "悠闲哼歌",
        }),

        // ---- 久没动：走神、发呆 ----
        new(Stale, new[]
        {
            "发呆碎碎念",
            "对屏碎碎念",
            "擦桌碎碎念",
            "原地踱步张望",
            "摇扇纳凉",
            "被吓一跳",
        }),

        // ---- 下班后：该过日子了 ----
        new(AfterHours, new[]
        {
            "吃晚餐",
            "涮火锅",
            "吃大闸蟹",
            "撸猫",
            "荡秋千",
            "放烟花",
            "放孔明灯",
            "摇扇纳凉",
        }),

        // ---- 今天的收获：素材自带的"钱包"梗 ----
        new(PurseRich, new[] { "余额-钱袋满溢", "余额-金袋叮当", "收红包" }),
        new(PurseMid, new[] { "余额-钱袋如常", "余额-数金皱眉" }),
        new(PurseEmpty, new[] { "余额-分文不剩", "余额-袋空如洗" }),
    };

    /// <summary>全部 hint（去重）—— 自检拿它跟真实素材清单核对覆盖率。</summary>
    public static IReadOnlyList<string> AllHints { get; } =
        Preferences.SelectMany(p => p.Hints).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>某个状态偏好的 hint（诊断输出用）。</summary>
    public static IReadOnlyList<string> HintsOf(string state)
        => Preferences.Where(p => p.State == state).SelectMany(p => p.Hints).ToList();

    /// <summary>
    /// 每个动作的权重。没被任何状态命中的一律 1.0（永远有机会出现）。
    ///
    /// 返回的字典包含**传进来的每一个动作** —— 调用方不用再补默认值，
    /// 也方便自测直接断言"某个动作在这个状态下权重是几倍"。
    /// </summary>
    public static IReadOnlyDictionary<string, double> Weigh(
        IEnumerable<string> actions, Context ctx)
    {
        var hits = MatchedHints(StatesOf(ctx));
        var result = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var action in actions)
        {
            double weight = 1.0;
            foreach (var hint in hits)
            {
                if (action.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    weight = Math.Max(weight, PreferredWeight);
                }
            }
            result[action] = weight;
        }

        return result;
    }

    /// <summary>当前状态下被抬权的那些 hint。</summary>
    private static List<string> MatchedHints(IReadOnlyList<string> states)
    {
        var hits = new List<string>();
        foreach (var pref in Preferences)
        {
            if (states.Contains(pref.State, StringComparer.Ordinal))
                hits.AddRange(pref.Hints);
        }
        return hits;
    }

    // ==================================================================
    // 挑选
    // ==================================================================

    /// <summary>
    /// 加权随机挑一个动作。<paramref name="actions"/> 为空返回 null
    /// （调用方据此跳过，而不是硬编码假设素材一定在）。
    ///
    /// <paramref name="recent"/> 是最近播过的动作（新的在前），里面的会降权 ——
    /// 只降权不清零，所以素材很少时它不会把人锁死在某几个上。
    /// </summary>
    public static string? Pick(
        IReadOnlyList<string> actions,
        Context ctx,
        IReadOnlyList<string> recent,
        Random rng)
    {
        if (actions.Count == 0) return null;

        var weights = Weigh(actions, ctx);

        // 只看最近的 RecentWindow 个：更早的已经过去，不该继续压着它
        var recentSet = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < recent.Count && i < RecentWindow; i++)
            recentSet.Add(recent[i]);

        double total = 0;
        var table = new double[actions.Count];
        for (int i = 0; i < actions.Count; i++)
        {
            var w = weights.TryGetValue(actions[i], out var v) ? v : 1.0;
            if (recentSet.Contains(actions[i])) w *= RecentPenalty;
            table[i] = w;
            total += w;
        }

        if (total <= 0) return actions[rng.Next(actions.Count)];

        var roll = rng.NextDouble() * total;
        for (int i = 0; i < actions.Count; i++)
        {
            roll -= table[i];
            if (roll <= 0) return actions[i];
        }

        // 浮点累减的尾巴，落在最后一个身上；不该走到这里，但要有确定行为
        return actions[actions.Count - 1];
    }

    /// <summary>
    /// 把"当前状态 → 挑中了什么"写成一行可读文本，诊断/日志用。
    /// 出问题时这一行能直接回答"为什么今天总在叹气"。
    /// </summary>
    public static string Explain(Context ctx)
    {
        var states = StatesOf(ctx);
        var labels = states.Select(Describe);
        return $"状态 [{string.Join("、", labels)}]"
             + $"（{ctx.Now:HH:mm} 记录{ctx.Entries} 未收尾{ctx.OpenEntries} 文件{ctx.Files}）";
    }
}
