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
    public string? CarriedToDate { get; set; }

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
            CarriedFromDate = sourceCreatedAt.ToString("yyyy-MM-dd"),
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
