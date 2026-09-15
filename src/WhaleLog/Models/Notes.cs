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

    public bool Done
    {
        get => _done;
        set
        {
            if (Set(ref _done, value))
                DoneAt = value ? DateTime.Now : null;
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
        sb.AppendLine($"files: {fileList.Count}");
        sb.AppendLine($"workedSeconds: {TotalSeconds}");
        sb.AppendLine($"updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {Date} 工作记录");
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
                var meta = e.Category;
                if (!string.IsNullOrWhiteSpace(e.Tag)) meta += $" / {e.Tag}";
                if (e.Done && !string.IsNullOrWhiteSpace(e.DurationText))
                    meta += $" / 耗时 {e.DurationText}";
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
