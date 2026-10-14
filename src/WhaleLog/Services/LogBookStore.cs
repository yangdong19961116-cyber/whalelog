using System.IO;
using System.Text.Json;
using WhaleLog.Models;

namespace WhaleLog.Services;

/// <summary>
/// 笔记与文件索引的唯一读写入口。
///
/// 落盘策略（借鉴 PaperTodo 的防损坏做法）：
///   index.json        结构化索引（笔记条目 + 文件记录 + 版本链），程序读的是它
///   index.backup.json 每次成功写入前的上一版，用于索引损坏时回滚
///   notes/yyyy-MM-dd.md  人可读的每日笔记，每次改动重新生成，方便直接翻看/进 Git
///
/// 写文件一律「先写 .tmp 再替换」，避免异常退出留下半截文件。
/// </summary>
public sealed class LogBookStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // 默认编码器会把中文转义成 \u62A5\u544A 这种形式。索引文件是给用户看的本地文件，
        // 保持可读比"防御性转义"重要得多（这份 JSON 不会被拼进 HTML）。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private bool _dirty;

    public LogBook Book { get; private set; } = new();

    public event Action? Changed;

    public void Load()
    {
        AppPaths.EnsureCreated();
        lock (_gate)
        {
            Book = ReadIndex() ?? new LogBook();
            // 索引里的文件路径可能已经变了，启动时校正一次
            RefreshMissingFlags();
            // 老数据没有 progress 字段，把「完成」和「进度」校准成互相一致（只改内存，不写盘）
            CarryOverService.Normalize(Book);
        }
    }

    private static LogBook? ReadIndex()
    {
        foreach (var path in new[] { AppPaths.IndexFile, AppPaths.IndexFile + ".backup" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text)) continue;
                var book = Deserialize(text);
                if (book != null) return book;
            }
            catch
            {
                // 主索引损坏就试备份
            }
        }
        return null;
    }

    /// <summary>
    /// 从 JSON 文本读一份索引，口径和 <see cref="Load"/> 完全一致（含完成/进度的归一化）。
    ///
    /// 单独开一个口子是给自测用的：老数据的 `done: true` 没有 progress 字段，
    /// "读进来之后到底补没补成 100%" 是这次改动的关键风险，必须能直接构造旧格式来验，
    /// 而不是在自测里再抄一份序列化选项（抄了就会各走各的）。
    /// </summary>
    public static LogBook? Deserialize(string json)
    {
        var book = JsonSerializer.Deserialize<LogBook>(json, JsonOpts);
        if (book != null) CarryOverService.Normalize(book);
        return book;
    }

    /// <summary>立刻落盘并重建当天 Markdown。</summary>
    public void Save()
    {
        lock (_gate)
        {
            try
            {
                AppPaths.EnsureCreated();

                // 备份上一版
                if (File.Exists(AppPaths.IndexFile))
                {
                    try { File.Copy(AppPaths.IndexFile, AppPaths.IndexFile + ".backup", overwrite: true); }
                    catch { /* 备份失败不阻塞主流程 */ }
                }

                var json = JsonSerializer.Serialize(Book, JsonOpts);
                WriteAtomic(AppPaths.IndexFile, json);

                // 每天一个 Markdown，人可读：当天事项 + 当天归档的文件清单
                foreach (var day in Book.Days)
                {
                    if (!DateTime.TryParse(day.Date, out var d)) continue;
                    var dayFiles = Book.Files.Where(f => f.AddedDate == day.Date);
                    WriteAtomic(
                        Path.Combine(AppPaths.NotesDir, AppPaths.ToDayFileName(d)),
                        day.ToMarkdown(dayFiles));
                }

                _dirty = false;
            }
            catch
            {
                // 落盘失败时内存数据仍然可用，下次改动会再试
            }
        }

        Changed?.Invoke();
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>标脏；真正的写盘由调用方在合适时机触发 Save()。</summary>
    public void MarkDirty() => _dirty = true;

    public bool IsDirty => _dirty;

    /// <summary>
    /// 确保今天有一份笔记文件，并在缺失时补写。
    /// 这样做有两个好处：notes 目录里一眼就能看到当天文件；
    /// 用户手工删掉 md 之后重启程序也能自动恢复。
    /// </summary>
    public void EnsureToday()
    {
        var today = DateTime.Today;
        bool isNew = Book.FindDay(today) == null;
        if (isNew) Book.GetOrCreateDay(today);

        var path = Path.Combine(AppPaths.NotesDir, AppPaths.ToDayFileName(today));
        if (isNew || !File.Exists(path))
            Save();
        else
        {
            // 文件在但内容可能是旧的（比如上次异常退出），重写一次成本很低
            try
            {
                var day = Book.FindDay(today)!;
                File.WriteAllText(path, day.ToMarkdown(FilesOnDate(today)));
            }
            catch
            {
                // 写不进去不影响启动
            }
        }
    }

    public DayNote GetOrCreateDay(DateTime date) => Book.GetOrCreateDay(date);

    public IEnumerable<FileRecord> FilesOn(string date)
        => Book.Files.Where(f => f.AddedDate == date);

    public IEnumerable<FileRecord> FilesOnDate(DateTime date)
        => FilesOn(date.ToString("yyyy-MM-dd"));

    public FileRecord? FindFileById(string id) => Book.Files.FirstOrDefault(f => f.Id == id);

    /// <summary>
    /// 同一个路径在**某一天**的那条归档记录。
    ///
    /// 同一个文件可以在不同日期各留一条记录 —— 9 月 16 日归档一次、9 月 17 日改了再归档一次，
    /// 那是两个独立的动作，分别记在当天，翻哪一天都看得到。
    /// 所以"这一天归档过它没有"必须连日期一起问，只按路径问会拿到别的日子那条。
    /// </summary>
    public FileRecord? FindFileByPathOn(string path, string date)
    {
        var full = SafeFullPath(path);
        return Book.Files.FirstOrDefault(
            f => string.Equals(f.FullPath, full, StringComparison.OrdinalIgnoreCase) && f.AddedDate == date);
    }

    /// <summary>
    /// 这个路径最近的一条记录。
    ///
    /// 一个路径可能有多条（见上），这里给的是最新的那条：
    /// 判断"内容有没有变"要跟最近一次归档比，跟最早那次比没有意义。
    /// </summary>
    public FileRecord? FindFileByPath(string path)
    {
        var full = SafeFullPath(path);
        return Book.Files
            .Where(f => string.Equals(f.FullPath, full, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.AddedAt)
            .ThenByDescending(f => f.Version)
            .FirstOrDefault();
    }

    /// <summary>同一个版本链上的所有记录，按版本号升序。</summary>
    public List<FileRecord> VersionChain(string versionGroupKey)
        => Book.Files
            .Where(f => f.VersionGroupKey == versionGroupKey)
            .OrderBy(f => f.Version)
            .ThenBy(f => f.AddedAt)
            .ToList();

    /// <summary>按天 / 全部两个维度统计用。</summary>
    public IEnumerable<FileRecord> QueryFiles(string? dateOrNull)
        => string.IsNullOrEmpty(dateOrNull)
            ? Book.Files.OrderByDescending(f => f.AddedAt)
            : Book.Files.Where(f => f.AddedDate == dateOrNull).OrderByDescending(f => f.AddedAt);

    public void RefreshMissingFlags()
    {
        foreach (var f in Book.Files)
        {
            try { f.IsMissing = !File.Exists(f.FullPath); }
            catch { f.IsMissing = true; }
        }
    }

    public static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    /// <summary>数据目录里的文件被用户手工删掉时，把失效记录摘掉。</summary>
    public int RemoveMissing()
    {
        var dead = Book.Files.Where(f => f.IsMissing).ToList();
        foreach (var f in dead) Book.Files.Remove(f);
        if (dead.Count > 0) Save();
        return dead.Count;
    }
}
