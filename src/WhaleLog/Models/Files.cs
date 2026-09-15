namespace WhaleLog.Models;

/// <summary>一条文件版本记录。</summary>
public sealed class FileVersion
{
    public int Version { get; set; } = 1;
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastWriteTime { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.Now;
    public string Path { get; set; } = "";

    /// <summary>开启快照模式时，归档副本所在位置。</summary>
    public string? SnapshotPath { get; set; }

    /// <summary>内容与上一版完全一致（哈希相同），不是真正的新版本。</summary>
    public bool IdenticalToPrevious { get; set; }

    public bool SampledHash { get; set; }
}

/// <summary>被拖入归档的一个文件。原文件始终原地不动。</summary>
public sealed class FileRecord : ObservableObject
{
    private string _category = "未分类";
    private string _note = "";
    private bool _isMissing;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string FileName { get; set; } = "";

    /// <summary>文件完整所在位置。</summary>
    public string FullPath { get; set; } = "";

    public string Extension { get; set; } = "";

    public long SizeBytes { get; set; }

    public DateTime LastWriteTime { get; set; }

    public string Sha256 { get; set; } = "";

    public string Category
    {
        get => _category;
        set => Set(ref _category, value);
    }

    /// <summary>命中的规则名，便于回看「为什么归到这一类」。</summary>
    public string MatchedRule { get; set; } = "";

    /// <summary>版本链分组键：归一化后的主文件名 + 扩展名。</summary>
    public string VersionGroupKey { get; set; } = "";

    public int Version { get; set; } = 1;

    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>归属日期（yyyy-MM-dd），用于按天筛选。</summary>
    public string AddedDate { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");

    public string Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    /// <summary>原文件已不在原位置（被移动或删除）。</summary>
    public bool IsMissing
    {
        get => _isMissing;
        set => Set(ref _isMissing, value);
    }

    public string? SnapshotPath { get; set; }

    /// <summary>关联的笔记条目 Id，可为空。</summary>
    public string? EntryId { get; set; }

    public List<FileVersion> History { get; set; } = new();

    public string ParentFolder
    {
        get
        {
            try { return System.IO.Path.GetDirectoryName(FullPath) ?? ""; }
            catch { return ""; }
        }
    }

    public string SizeText => FormatSize(SizeBytes);

    public string VersionText => $"v{Version}";

    /// <summary>归档归属日期的显示文本，例如「9月11日」；跨年时带年份。解析不了返回空串。</summary>
    public string AddedDateText
    {
        get
        {
            if (!DateTime.TryParse(AddedDate, out var d)) return "";
            return d.Year == DateTime.Today.Year ? $"{d:M月d日}" : $"{d:yyyy年M月d日}";
        }
    }

    /// <summary>
    /// 列表里显示的归档时间戳：归属日期 + 拖入时刻。
    /// 刻意用 AddedDate 而不是 AddedAt 的日期 —— 分组栏头用的是 AddedDate，
    /// 两处口径必须一致，否则会出现"栏头写 9月7日、行里写 9月11日"。
    /// </summary>
    public string AddedStampText
    {
        get
        {
            var time = AddedAt.ToString("HH:mm");
            var date = AddedDateText;
            return date.Length == 0 ? time : $"{date} {time}";
        }
    }

    public string StatusText => IsMissing ? "文件已不在原位置" : FullPath;

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }
}

/// <summary>
/// 分类规则。借鉴 DropIt 的「协议 = 筛选条件 + 动作」模型：
/// 三个条件维度全部满足才算命中，Priority 小的先匹配。
/// </summary>
public sealed class ClassifyRule
{
    public string Name { get; set; } = "";

    public string Category { get; set; } = "";

    public int Priority { get; set; } = 100;

    public bool Enabled { get; set; } = true;

    /// <summary>空列表 = 不限扩展名。</summary>
    public List<string> Extensions { get; set; } = new();

    /// <summary>文件名需包含其中任意一个关键词（不区分大小写）。空列表 = 不限。</summary>
    public List<string> NameKeywords { get; set; } = new();

    /// <summary>所在路径需包含其中任意一个关键词。空列表 = 不限。</summary>
    public List<string> PathKeywords { get; set; } = new();

    public bool HasExtensionFilter => Extensions.Count > 0;
    public bool HasNameFilter => NameKeywords.Count > 0;
    public bool HasPathFilter => PathKeywords.Count > 0;
    public bool HasAnyFilter => HasExtensionFilter || HasNameFilter || HasPathFilter;
}

public enum IntakeStatus
{
    /// <summary>首次归档。</summary>
    Added,
    /// <summary>同一文件的新版本。</summary>
    NewVersion,
    /// <summary>内容与已有版本完全一致，未产生新版本。</summary>
    Unchanged,
    /// <summary>同一路径同一内容，重复拖入。</summary>
    Duplicate,
    /// <summary>文件不存在或读取失败。</summary>
    Failed,
    /// <summary>目录展开后没有可用文件。</summary>
    Nothing,
}

public sealed class IntakeResult
{
    public IntakeStatus Status { get; init; }
    public string Path { get; init; } = "";
    public FileRecord? Record { get; init; }
    public string Message { get; init; } = "";
    public bool Success => Status is IntakeStatus.Added or IntakeStatus.NewVersion;
}

/// <summary>整库索引：所有日期的记录 + 所有归档文件。落盘为 index.json。</summary>
public sealed class LogBook
{
    public int SchemaVersion { get; set; } = 1;

    public List<DayNote> Days { get; set; } = new();

    public List<FileRecord> Files { get; set; } = new();

    public DayNote GetOrCreateDay(DateTime date)
    {
        var key = date.ToString("yyyy-MM-dd");
        var day = Days.FirstOrDefault(d => d.Date == key);
        if (day == null)
        {
            day = new DayNote { Date = key };
            Days.Add(day);
        }
        return day;
    }

    public DayNote? FindDay(DateTime date)
        => Days.FirstOrDefault(d => d.Date == date.ToString("yyyy-MM-dd"));
}
