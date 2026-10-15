using System.Globalization;
using System.Text;

namespace WhaleLog.Models;

/// <summary>一条手工记录的工作事项。</summary>
public sealed class NoteEntry : ObservableObject
{
    private string _text = "";
    private string _category = NoteCategories.Work;
    private string _tag = "";
    private bool _done;
    private DateTime? _doneAt;
    private int _durationSeconds;
    private int _progress;
    private int _resumeProgress;
    private string? _carriedToDate;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public string Category
    {
        get => _category;
        set => Set(ref _category, value);
    }

    /// <summary>自由标签，逗号分隔。</summary>
    public string Tag
    {
        get => _tag;
        set => Set(ref _tag, value);
    }

    /// <summary>
    /// 完成度 0–100。它既是进度条的显示来源，也是「完成」的判定口径：
    /// **Progress == 100 就等于 Done == true**，两边永远一起变（见下面两个 setter）。
    /// </summary>
    public int Progress
    {
        get => _progress;
        set
        {
            var v = Math.Clamp(value, 0, 100);
            if (_progress == v) return;

            _progress = v;
            // 「取消勾选回到上次拖到的位置」靠的就是这个：只要拖到 <100 的位置就记下来。
            // 拖到 100 刻意不覆盖它 —— 否则打个勾就把原来的位置冲掉了。
            if (v < 100) ResumeProgress = v;

            Raise(nameof(Progress));
            Raise(nameof(ProgressText));

            // 拖到满格自动打勾、拖下来自动取消（Done 的 setter 负责 DoneAt 和耗时）
            Done = v >= 100;
        }
    }

    /// <summary>
    /// 取消勾选时进度回到的位置（= 最后一次手动拖到的百分比）。
    ///
    /// 单独存一个字段而不是「取消勾选就归 0」：打勾会把进度顶到 100，
    /// 原来拖到的 80% 若不记下来，勾错一次再取消就丢了。
    /// 它跟着条目一起落盘，重启后依然有效。
    /// </summary>
    public int ResumeProgress
    {
        get => _resumeProgress;
        set => Set(ref _resumeProgress, Math.Clamp(value, 0, 99));
    }

    public string ProgressText => $"{_progress}%";

    /// <summary>已完成。打勾 = 进度 100；取消勾选 = 回到上次拖到的位置。</summary>
    public bool Done
    {
        get => _done;
        set
        {
            if (!Set(ref _done, value)) return;

            DoneAt = value ? DateTime.Now : null;

            // 这里刻意直接写字段、不调 Progress 的 setter —— 调了会绕回去再设一次 Done，成环。
            var target = value ? 100 : ResumeProgress;
            if (_progress != target)
            {
                _progress = target;
                Raise(nameof(Progress));
                Raise(nameof(ProgressText));
            }
        }
    }

    public DateTime? DoneAt
    {
        get => _doneAt;
        set
        {
            if (Set(ref _doneAt, value))
            {
                DurationSeconds = value.HasValue
                    ? (int)Math.Max(0, (value.Value - CreatedAt).TotalSeconds)
                    : 0;
            }
        }
    }

    public int DurationSeconds
    {
        get => _durationSeconds;
        set => Set(ref _durationSeconds, value);
    }

    // ---------------------------------------------------------------
    // 顺延（当天没勾完 → 次日复制一份、带着进度过来）
    // ---------------------------------------------------------------

    /// <summary>来源日期（yyyy-MM-dd）。非空 = 这是一条顺延过来的副本。</summary>
    public string? CarriedFromDate { get; set; }

    /// <summary>来源条目 Id。用来判断今天是不是已经有这条的副本，避免重复顺延。</summary>
    public string? CarriedFromId { get; set; }

    /// <summary>本条已经顺延到了哪一天（yyyy-MM-dd）。打上之后就不再重复搬运。</summary>
    public string? CarriedToDate
    {
        get => _carriedToDate;
        set
        {
            // 用属性而不是自动属性：过去某天的记录被改成未完成时会**当场**顺延到今天
            // （见 MainWindow.TrySyncUnfinishedPastEntryToToday），那一刻用户正看着这条，
            // 行尾的「已顺延至 X」得立刻冒出来 —— 自动属性不发通知，要等重新加载列表才显示。
            if (!Set(ref _carriedToDate, value)) return;
            Raise(nameof(IsRolledForward));
            Raise(nameof(RolledToText));
        }
    }

    /// <summary>这是一条顺延过来的副本。</summary>
    public bool IsCarried => !string.IsNullOrEmpty(CarriedFromDate);

    /// <summary>已经被顺延走了。翻回旧日期时给个交代，免得看着像任务莫名消失。</summary>
    public bool IsRolledForward => !string.IsNullOrEmpty(CarriedToDate);

    public string CarriedFromText => FriendlyDate(CarriedFromDate);

    /// <summary>徽标文案；不是顺延来的就是空串（空串让 NonEmptyToVis 把它收起来）。</summary>
    public string CarriedFromLabel => IsCarried ? $"顺延自 {CarriedFromText}" : "";

    public string RolledToText
    {
        get
        {
            var text = FriendlyDate(CarriedToDate);
            return text.Length == 0 ? "" : $"已顺延至 {text}";
        }
    }

    private static string FriendlyDate(string? date)
    {
        if (!DateTime.TryParse(date, out var d)) return "";
        return d.Year == DateTime.Today.Year ? $"{d:M月d日}" : $"{d:yyyy年M月d日}";
    }

    /// <summary>
    /// 顺延到 targetDay 的副本：文字/分类/标签/进度照搬，时间另起。
    /// 源条目**一个字都不改**（只在它身上戳一个「已顺延」的时间戳，见 CarriedToDate）。
    /// </summary>
    public NoteEntry CloneForCarry(DateTime targetDay)
    {
        var sourceCreatedAt = CreatedAt;
        return new NoteEntry
        {
            Text = Text,
            Category = Category,
            Tag = Tag,
            Progress = Progress,
            ResumeProgress = ResumeProgress,
            CreatedAt = targetDay.Date == DateTime.Today
                ? DateTime.Now
                : targetDay.Date.AddHours(9),
            CarriedFromDate = sourceCreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            CarriedFromId = Id,
        };
    }

    /// <summary>
    /// 把「完成」和「进度」校准到互相一致。
    ///
    /// 老数据没有 progress 字段：`done: true` 的那种必须补成 100，
    /// 否则一条已完成的事项会显示成 0% 的进度条。反过来的脏数据（没完成却是 100）
    /// 以「没完成」为准退回去。
    /// </summary>
    public void Normalize()
    {
        if (_done)
        {
            if (_progress == 100) return;
            _progress = 100;
        }
        else if (_progress >= 100)
        {
            _progress = _resumeProgress;
        }
        else
        {
            // 没完成时，当前进度就是「取消勾选该回到哪」
            _resumeProgress = _progress;
            return;
        }

        Raise(nameof(Progress));
        Raise(nameof(ProgressText));
    }

    /// <summary>本条记录关联的文件（FileRecord.Id）。</summary>
    public List<string> FileIds { get; set; } = new();

    public string TimeText => CreatedAt.ToString("HH:mm");

    public string DurationText
    {
        get
        {
            var d = TimeSpan.FromSeconds(DurationSeconds);
            if (DurationSeconds <= 0) return "";
            return d.TotalHours >= 1
                ? $"{(int)d.TotalHours}h{d.Minutes:D2}m"
                : $"{d.Minutes}m";
        }
    }

    /// <summary>
    /// 行尾那串元信息的组成部分：分类、标签、顺延、耗时或进度。
    ///
    /// `notes\*.md`、日报、周报三处共用它 —— 同一件事只保留一份口径，
    /// 免得改了一处、另外两处还是老写法。
    /// </summary>
    public List<string> MetaParts()
    {
        var parts = new List<string> { Category };
        if (!string.IsNullOrWhiteSpace(Tag)) parts.Add(Tag);
        if (IsCarried) parts.Add($"顺延自 {CarriedFromText}");
        if (IsRolledForward) parts.Add(RolledToText);

        if (Done)
        {
            if (!string.IsNullOrWhiteSpace(DurationText)) parts.Add($"耗时 {DurationText}");
        }
        else if (Progress > 0)
        {
            parts.Add($"进度 {Progress}%");
        }

        return parts;
    }
}

/// <summary>
/// 「当天完成度」的算法 —— 只有这一份。
///
/// 界面上列表底部那一行、日报/周报、落盘的 index.json 与 notes\*.md 四处都要这个数字，
/// 各算各的迟早会算出四个不一样的数（这个项目已经吃过一次「同一天两个日期口径」的亏）。
/// </summary>
public static class DayCompletion
{
    /// <summary>
    /// 一组事项的平均进度（0–100）；一条都没有时是 null（"今天没记录"和"今天做到 0%"是两回事）。
    ///
    /// 取平均而不是"勾了几条 / 共几条"：进度条本来就是用来表达"干到哪儿了"的，
    /// 一条 95% 一条 5% 跟一条 50% 一条 50% 不该算成同一个数。
    /// 打勾的条目进度恒为 100（见 NoteEntry），所以这里不用再给 Done 特殊照顾。
    /// </summary>
    public static int? Average(IEnumerable<NoteEntry> entries)
    {
        var list = entries as ICollection<NoteEntry> ?? entries.ToList();
        if (list.Count == 0) return null;
        return (int)Math.Round(list.Average(e => e.Progress));
    }

    /// <summary>
    /// 该显示哪一个：今天实时算（用户正在拖，必须跟着动），过去的日子用收工时的定格值。
    /// 老数据（这个字段是本版才加的）没有定格值，就退回实时算 —— 那正好就是它当年落盘的样子。
    /// </summary>
    public static int? Display(int? live, int? frozen, bool isToday)
    {
        if (live == null) return null;
        return isToday ? live : frozen ?? live;
    }
}

/// <summary>一天的记录。</summary>
public sealed class DayNote : ObservableObject
{
    private string _summary = "";

    public string Date { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");

    /// <summary>当天小结，下班前手写或自动汇总。</summary>
    public string Summary
    {
        get => _summary;
        set => Set(ref _summary, value);
    }

    public List<NoteEntry> Entries { get; set; } = new();

    public int DoneCount => Entries.Count(e => e.Done);

    public int OpenCount => Entries.Count(e => !e.Done);

    /// <summary>这天有几条是从以前顺延过来的。</summary>
    public int CarriedCount => Entries.Count(e => e.IsCarried);

    /// <summary>未完成事项的平均进度（0–100），没有未完成的就是 100。</summary>
    public int OpenProgress
    {
        get
        {
            var open = Entries.Where(e => !e.Done).ToList();
            return open.Count == 0 ? 100 : (int)Math.Round(open.Average(e => e.Progress));
        }
    }

    // ---------------------------------------------------------------
    // 当天完成度（列表底部那一行 / 日报 / notes\*.md）
    // ---------------------------------------------------------------

    /// <summary>
    /// 这一天收工时的完成度（0–100）。null = 那天没有事项，或者老数据还没定过格。
    ///
    /// 它不是"当前状态"，而是一份**结账单**：今天这一档随每次改动刷新（每次落盘前同步），
    /// 过去的日子在跨零点时定格一次，之后翻回去看到的永远是那个数 —— 见 CompletionService。
    /// </summary>
    public int? Completion { get; set; }

    /// <summary>定格时间（yyyy-MM-dd HH:mm:ss）。只在这一天真的收工结账时写上（见 Freeze）。</summary>
    public string? CompletionFrozenAt { get; set; }

    /// <summary>
    /// 这一天的完成度已经收工结账了。
    ///
    /// 和上面的时间戳分开是有原因的：装上新版之后第一次跑，会把一两个月前的日子
    /// 一起补算（那几天程序根本没开着），这时候**值要写、时间戳不能写** ——
    /// 写一个"今天"的时刻会让人以为那天半夜真的结过账，那是假的。
    /// 所以"结没结账"用这个布尔记，"几点结的"只记真正在零点附近结的那次。
    /// </summary>
    public bool CompletionSettled { get; set; }

    /// <summary>按当前条目实时算出来的完成度；没有事项的一天是 null。</summary>
    public int? LiveCompletion => DayCompletion.Average(Entries);

    public bool IsCompletionSettled => CompletionSettled;

    /// <summary>有没有记下收工的那一刻。</summary>
    public bool HasFrozenTime => !string.IsNullOrEmpty(CompletionFrozenAt);

    /// <summary>
    /// 收工定格：把此刻的实时完成度写死在 <see cref="Completion"/> 上。
    ///
    /// 幂等 —— 已经结过账且值没变就什么都不做（跨零点的轮询每 20 秒一次，
    /// 重复跑不该把历史数字改来改去，也不该白白触发落盘）。返回是否真的写了东西。
    ///
    /// 时间戳只在"这一天的第二天凌晨前后"才记：补算历史（<paramref name="at"/> 远在几天之后）
    /// 时留空，界面上就会说"没有记下收工时刻"，而不是编一个时间出来。
    /// </summary>
    public bool Freeze(DateTime at)
    {
        var live = LiveCompletion;
        if (live == null) return false; // 一条记录都没有的日子不记这个数字
        if (CompletionSettled && Completion == live) return false;

        Completion = live;
        CompletionSettled = true;

        var day = DateTime.TryParse(Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.Date
            : at.Date;

        CompletionFrozenAt = at.Date <= day.AddDays(1)
            ? at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : null;

        return true;
    }

    /// <summary>定格时间给人看的写法（"10月13日 23:59"）；没定过格就是空串。</summary>
    public string CompletionFrozenText
    {
        get
        {
            if (!DateTime.TryParse(
                    CompletionFrozenAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            {
                return "";
            }
            return $"{t:M月d日 HH:mm}";
        }
    }

    /// <summary>界面上该显示的那个数字（今天实时、过去的日子用定格值）。</summary>
    public int? DisplayCompletion(DateTime today)
        => DayCompletion.Display(LiveCompletion, Completion, Date == Key(today));

    /// <summary>yyyy-MM-dd。日期是要落盘、要跨机器比的，一律用不变文化格式化。</summary>
    private static string Key(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public int TotalSeconds => Entries.Sum(e => e.DurationSeconds);

    public string TotalDurationText
    {
        get
        {
            var d = TimeSpan.FromSeconds(TotalSeconds);
            return d.TotalHours >= 1
                ? $"{(int)d.TotalHours}h{d.Minutes:D2}m"
                : $"{d.Minutes}m";
        }
    }

    /// <summary>渲染成 Markdown（落盘用）。files 传当天的归档文件，会额外附一节文件清单。</summary>
    public string ToMarkdown(IEnumerable<FileRecord>? files = null)
    {
        var fileList = files?.OrderBy(f => f.AddedAt).ThenBy(f => f.FileName).ToList()
                       ?? new List<FileRecord>();

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"date: {Date}");
        sb.AppendLine($"entries: {Entries.Count}");
        sb.AppendLine($"done: {DoneCount}");
        sb.AppendLine($"carried: {CarriedCount}");
        sb.AppendLine($"openProgress: {OpenProgress}");
        // 当天完成度：收工结过账的写定格值，今天（还没结账）写实时值。
        // 一条记录都没有的日子不写这两行 —— 那个数字不存在，写个 0 会被当成"干了 0%"。
        var completion = Completion ?? LiveCompletion;
        if (completion.HasValue) sb.AppendLine($"completion: {completion}");
        if (HasFrozenTime) sb.AppendLine($"frozenAt: {CompletionFrozenAt}");
        sb.AppendLine($"files: {fileList.Count}");
        sb.AppendLine($"workedSeconds: {TotalSeconds}");
        sb.AppendLine($"updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {Date} 工作记录");
        sb.AppendLine();
        sb.AppendLine(
            $"<!-- 进度是 0–100 的百分比，勾选即 100%。未勾完的事项次日会自动顺延到新的一天，带进度过去。 -->");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(Summary))
        {
            sb.AppendLine("## 今日小结");
            sb.AppendLine();
            sb.AppendLine(Summary.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("## 事项");
        sb.AppendLine();
        if (Entries.Count == 0)
        {
            sb.AppendLine("_（今天还没有记录）_");
        }
        else
        {
            foreach (var e in Entries.OrderBy(x => x.CreatedAt))
            {
                var box = e.Done ? "x" : " ";
                var meta = string.Join(" / ", e.MetaParts());
                sb.AppendLine($"- [{box}] `{e.TimeText}` {e.Text}  _{meta}_");
            }

            // 一整天的完成度：所有事项进度的平均，勾选即 100% —— 和列表底部那一行是同一个数。
            // 结过账的写清状态：编不出时间就直说"没有记下收工时刻"，不假装那天半夜结过账。
            if (completion.HasValue)
            {
                var tail = !CompletionSettled
                    ? "，还没结账（随改动更新）"
                    : HasFrozenTime
                        ? $"，{CompletionFrozenAt} 收工定格"
                        : "，按当天留下的记录回算（没有记下收工时刻）";
                sb.AppendLine();
                sb.AppendLine($"> 当天完成度 **{completion}%** —— {Entries.Count} 条事项进度的平均（勾选记 100%）{tail}");
            }
        }

        sb.AppendLine();

        if (fileList.Count > 0)
        {
            sb.AppendLine("## 归档文件");
            sb.AppendLine();
            sb.AppendLine($"_共 {fileList.Count} 个，按类别分组。原文件都在原位置，没有被移动过。_");
            sb.AppendLine();

            foreach (var group in fileList.GroupBy(f => f.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"### {group.Key}");
                sb.AppendLine();
                foreach (var f in group)
                {
                    var version = f.Version > 1 ? $" `v{f.Version}`" : "";
                    sb.AppendLine($"- **{f.FileName}**{version} · {f.SizeText} · {f.LastWriteTime:HH:mm}");
                    sb.AppendLine($"  - 位置：{Code(f.FullPath)}");
                    if (f.IsMissing) sb.AppendLine("  - ⚠ 文件已不在原位置");
                    if (!string.IsNullOrEmpty(f.MatchedRule)) sb.AppendLine($"  - 命中规则：{f.MatchedRule}");
                    if (!string.IsNullOrEmpty(f.SnapshotPath)) sb.AppendLine($"  - 快照：{Code(f.SnapshotPath)}");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>路径里出现反引号会破坏 Markdown 行内代码，替换掉最省事。</summary>
    private static string Code(string text) => $"`{text.Replace('`', '\'')}`";
}

public static class NoteCategories
{
    public const string Work = "工作";
    public const string Meeting = "会议";
    public const string Comm = "沟通";
    public const string Report = "汇报";
    public const string Debug = "排查";
    public const string Learn = "学习";
    public const string Other = "其他";

    public static readonly string[] All =
    {
        Work, Meeting, Comm, Report, Debug, Learn, Other,
    };
}
