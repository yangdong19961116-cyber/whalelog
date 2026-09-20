using System.IO;
using WhaleLog.Models;

namespace WhaleLog.Services;

/// <summary>
/// 拖拽归档的入口。
///
/// 安全底线（本文件的核心设计约束）：
///   1. 绝不移动、重命名、删除用户的原文件 —— 只读取元数据和算指纹。
///   2. 除了可选的归档快照副本，不做任何写操作。
///   3. 读不动的文件（占用中 / 无权限）不抛异常中断整批，单条标记失败继续处理下一个。
///
/// 归档记录的粒度：**一个路径 × 一天 = 一条记录**。
/// 同一个文件 9 月 16 日归档一次、9 月 17 日改了内容再归档一次，是两个独立动作，
/// 各记一条、各自属于那天 —— 翻 9 月 16 日看到 v1，翻 9 月 17 日看到 v2。
/// 同一天里反复改则只升当天那一条的版本号（不然改五次就刷出五行）。
/// 归属日期（AddedDate）是"某天有哪些归档文件"的唯一依据：当日页面、只看当天、
/// 全部视图分栏、月历圆点、日报周报、notes\*.md 看的全是它。
/// </summary>
public sealed class FileIntakeService
{
    private const int MaxFilesPerDrop = 800;

    private readonly LogBookStore _store;
    private readonly ClassifyService _classify;
    private readonly VersionService _version;
    private readonly SettingsService _settings;

    public FileIntakeService(
        LogBookStore store,
        ClassifyService classify,
        VersionService version,
        SettingsService settings)
    {
        _store = store;
        _classify = classify;
        _version = version;
        _settings = settings;
    }

    /// <summary>单个文件处理完成就立刻回调一次，界面可以边处理边刷进度。</summary>
    public event Action<IntakeResult>? FileProcessed;

    /// <summary>
    /// 把一批归档结果压成一句话。
    ///
    /// 主窗口的状态栏和悬浮窗的气泡都走这里 —— 两处各写一份的话，
    /// 同一次拖入在两个地方会显示成两种说法（一个说"新增 2"、另一个说"添加了 2 个文件"）。
    /// </summary>
    /// <param name="dateSuffix">可选后缀，例如「（记入 9月16日）」。</param>
    public static string Summarize(IReadOnlyList<IntakeResult> results, string? dateSuffix = null)
    {
        int added = results.Count(r => r.Status == IntakeStatus.Added);
        int versioned = results.Count(r => r.Status == IntakeStatus.NewVersion);
        int unchanged = results.Count(r => r.Status == IntakeStatus.Unchanged);
        int duplicate = results.Count(r => r.Status == IntakeStatus.Duplicate);
        int failed = results.Count(r => r.Status == IntakeStatus.Failed);

        var parts = new List<string>();
        if (added > 0) parts.Add($"新增 {added}");
        if (versioned > 0) parts.Add($"新版本 {versioned}");
        if (unchanged > 0) parts.Add($"内容未变 {unchanged}");
        if (duplicate > 0) parts.Add($"重复 {duplicate}");
        if (failed > 0) parts.Add($"失败 {failed}");

        if (parts.Count == 0) return "没有可归档的文件";
        return string.Join("，", parts) + (dateSuffix ?? "");
    }

    public Task<IReadOnlyList<IntakeResult>> IntakeAsync(
        IEnumerable<string> paths,
        DateTime? forDate = null,
        string? entryId = null,
        CancellationToken ct = default)
        => Task.Run(() => IntakeCore(paths, forDate, entryId, ct), ct);

    private IReadOnlyList<IntakeResult> IntakeCore(
        IEnumerable<string> paths,
        DateTime? forDate,
        string? entryId,
        CancellationToken ct)
    {
        var date = forDate ?? DateTime.Today;
        var dateKey = date.ToString("yyyy-MM-dd");

        var expanded = Expand(paths);
        if (expanded.Count == 0)
        {
            var nothing = new IntakeResult
            {
                Status = IntakeStatus.Nothing,
                Message = "没有找到可归档的文件（空文件夹或被过滤掉了）",
            };
            FileProcessed?.Invoke(nothing);
            return new[] { nothing };
        }

        // 确保这一天的记录存在。归档出来的文件要能落到当天笔记里，
        // 而不是只躺在索引中 —— 否则用户翻 notes 目录时会看不到今天动过哪些文件。
        _store.GetOrCreateDay(date);

        var results = new List<IntakeResult>(expanded.Count);
        var entry = string.IsNullOrEmpty(entryId)
            ? null
            : _store.Book.Days.SelectMany(d => d.Entries).FirstOrDefault(e => e.Id == entryId);

        foreach (var path in expanded)
        {
            ct.ThrowIfCancellationRequested();

            IntakeResult result;
            try
            {
                result = IntakeOne(path, dateKey, entry, ct);
            }
            catch (Exception ex)
            {
                result = new IntakeResult
                {
                    Status = IntakeStatus.Failed,
                    Path = path,
                    Message = $"读取失败：{ex.Message}",
                };
            }

            results.Add(result);
            FileProcessed?.Invoke(result);
        }

        _store.Save();
        return results;
    }

    private IntakeResult IntakeOne(string path, string dateKey, NoteEntry? entry, CancellationToken ct)
    {
        var full = LogBookStore.SafeFullPath(path);
        string fileName;
        FileInfo info;

        try
        {
            fileName = Path.GetFileName(full);
            info = new FileInfo(full);
            if (!info.Exists)
            {
                return new IntakeResult
                {
                    Status = IntakeStatus.Failed,
                    Path = full,
                    Message = "文件不存在或已被移动",
                };
            }
        }
        catch (Exception ex)
        {
            return new IntakeResult
            {
                Status = IntakeStatus.Failed,
                Path = full,
                Message = $"无法读取文件信息：{ex.Message}",
            };
        }

        ct.ThrowIfCancellationRequested();

        var hash = _version.ComputeHash(full);
        var groupKey = VersionService.BuildVersionGroupKey(fileName);

        // 同一个文件在不同日期归档，是几个**独立的动作**，各记一条。
        // 所以这里问的是"**这一天**归档过这个路径没有"，而不是"这个路径归档过没有" ——
        // 后者会让新版本永远粘在首次归档那天，产生它的那天反而查不到。
        // （dateKey 是这次归档的归属日期，跟着用户正在看的那一天走，不一定是字面的今天。）
        var existing = _store.FindFileByPathOn(full, dateKey);
        var chain = _store.VersionChain(groupKey);

        // 情况一：这一天归档过同一个路径，内容也没变 —— 重复拖入
        if (existing != null && string.Equals(existing.Sha256, hash.Hash, StringComparison.Ordinal))
        {
            existing.LastWriteTime = info.LastWriteTime;
            existing.SizeBytes = info.Length;
            existing.IsMissing = false;
            if (entry != null) LinkEntry(existing, entry);

            return new IntakeResult
            {
                Status = IntakeStatus.Duplicate,
                Path = full,
                Record = existing,
                Message = $"{fileName} 已在记录中，内容与 v{existing.Version} 一致",
            };
        }

        // 情况二：这一天归档过同一个路径、内容变了 —— 同一天里的"改了再存"，就地升版本。
        // 不另起一条：一天里存五次就刷出五行来，那不成记录成噪音了。
        if (existing != null)
        {
            int next = chain.Count == 0 ? existing.Version + 1 : chain.Max(x => x.Version) + 1;
            ApplyVersion(existing, next, hash, info, dateKey);
            if (entry != null) LinkEntry(existing, entry);

            return new IntakeResult
            {
                Status = IntakeStatus.NewVersion,
                Path = full,
                Record = existing,
                Message = $"{fileName} 内容有更新 → v{next}",
            };
        }

        // 情况三：这一天还没归档过这个路径。整条版本链上已有内容完全一致的版本 —— 不产生新版本
        var identical = chain.FirstOrDefault(x => string.Equals(x.Sha256, hash.Hash, StringComparison.Ordinal));
        if (identical != null)
        {
            return new IntakeResult
            {
                Status = IntakeStatus.Unchanged,
                Path = full,
                Record = identical,
                Message = $"{fileName} 与 v{identical.Version} 内容一致，未产生新版本",
            };
        }

        // 情况四：真正的新文件 / 新版本 —— 这一天单独记一条
        int version = chain.Count == 0 ? 1 : chain.Max(x => x.Version) + 1;
        var hit = _classify.Classify(fileName, full);

        var record = new FileRecord
        {
            FileName = fileName,
            FullPath = full,
            Extension = Path.GetExtension(fileName).ToLowerInvariant(),
            SizeBytes = info.Length,
            LastWriteTime = info.LastWriteTime,
            Sha256 = hash.Hash,
            Category = hit.Category,
            MatchedRule = hit.MatchedRule,
            VersionGroupKey = groupKey,
            Version = version,
            AddedAt = DateTime.Now,
            AddedDate = dateKey,
            IsMissing = false,
        };

        ApplyVersion(record, version, hash, info, dateKey, isInitial: true);
        if (entry != null) LinkEntry(record, entry);

        _store.Book.Files.Add(record);

        return new IntakeResult
        {
            Status = version == 1 ? IntakeStatus.Added : IntakeStatus.NewVersion,
            Path = full,
            Record = record,
            Message = version == 1
                ? $"{fileName} 已归档到「{hit.Category}」"
                : $"{fileName} 已归档到「{hit.Category}」→ v{version}",
        };
    }

    private void ApplyVersion(
        FileRecord record,
        int version,
        VersionService.HashResult hash,
        FileInfo info,
        string dateKey,
        bool isInitial = false)
    {
        record.Sha256 = hash.Hash;
        record.SizeBytes = info.Length;
        record.LastWriteTime = info.LastWriteTime;
        record.Version = version;
        record.IsMissing = false;

        // 归属日期跟着**这次**归档动作走，升版本也一样。
        // 只改版本号不动日期的话，新版本会一直挂在首次归档那天，
        // 而当天页面、"只看当天"、月历圆点、日报周报全是以归属日期为准的 ——
        // 结果就是"今天明明归档了，今天的页面却是空的"。
        record.AddedDate = dateKey;
        record.AddedAt = DateTime.Now;

        var history = record.History ??= new List<FileVersion>();
        if (isInitial && history.Count == 0)
        {
            history.Add(new FileVersion
            {
                Version = version,
                Sha256 = hash.Hash,
                SizeBytes = info.Length,
                LastWriteTime = info.LastWriteTime,
                RecordedAt = DateTime.Now,
                Path = info.FullName,
                SampledHash = hash.Sampled,
            });
        }
        else
        {
            var previous = history.OrderByDescending(v => v.Version).FirstOrDefault();
            history.Add(new FileVersion
            {
                Version = version,
                Sha256 = hash.Hash,
                SizeBytes = info.Length,
                LastWriteTime = info.LastWriteTime,
                RecordedAt = DateTime.Now,
                Path = info.FullName,
                SampledHash = hash.Sampled,
                IdenticalToPrevious = previous != null &&
                                      string.Equals(previous.Sha256, hash.Hash, StringComparison.Ordinal),
            });
        }

        if (_settings.Settings.SnapshotMode)
        {
            var snapshot = TrySnapshot(record, version, info);
            if (snapshot != null)
            {
                record.SnapshotPath = snapshot;
                var current = history.LastOrDefault(v => v.Version == version);
                if (current != null) current.SnapshotPath = snapshot;
            }
        }
    }

    /// <summary>
    /// 可选快照：把这一版复制到 archive\ 下，版本才真的"留下来了"。
    /// 复制失败不影响归档记录本身，只是没有副本可回滚。
    /// </summary>
    private string? TrySnapshot(FileRecord record, int version, FileInfo info)
    {
        try
        {
            var baseName = VersionService.SanitizeForPath(
                VersionService.NormalizeBaseName(record.FileName));
            var dir = Path.Combine(AppPaths.ArchiveDir, record.AddedDate, baseName);
            Directory.CreateDirectory(dir);

            var target = Path.Combine(dir, $"v{version}_{record.FileName}");
            if (File.Exists(target)) return target;

            File.Copy(info.FullName, target);
            return target;
        }
        catch
        {
            return null;
        }
    }

    private static void LinkEntry(FileRecord record, NoteEntry entry)
    {
        record.EntryId ??= entry.Id;
        if (!entry.FileIds.Contains(record.Id))
            entry.FileIds.Add(record.Id);
    }

    /// <summary>
    /// 把拖入的路径展开成文件列表：目录递归、跳过隐藏/系统项、
    /// 跳过重解析点（防止符号链接成环）、跳过自己的数据目录。
    /// </summary>
    private static List<string> Expand(IEnumerable<string> paths)
    {
        var files = new List<string>();

        // 末尾要补上分隔符再比。只比目录名本身的话，前缀相同的兄弟目录会被误判成
        // "在数据目录里面"而整批悄悄跳过 —— 数据目录是 C:\Logs 时，
        // 拖着 C:\Logs2\a.txt 进来会一个文件都收不上，且没有任何提示。
        var dataRoot = AppPaths.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;

        void AddFile(string file)
        {
            if (files.Count >= MaxFilesPerDrop) return;
            try
            {
                if (file.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase)) return;
                var attrs = File.GetAttributes(file);
                if (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System)) return;
                files.Add(file);
            }
            catch
            {
                // 读不到属性就当它不存在
            }
        }

        void Walk(string dir, int depth)
        {
            if (files.Count >= MaxFilesPerDrop || depth > 12) return;
            try
            {
                var info = new DirectoryInfo(dir);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
                if (info.Attributes.HasFlag(FileAttributes.System)) return;

                foreach (var child in info.EnumerateFiles())
                    AddFile(child.FullName);

                if (files.Count >= MaxFilesPerDrop) return;

                foreach (var sub in info.EnumerateDirectories())
                {
                    if (sub.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                    if (sub.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    Walk(sub.FullName, depth + 1);
                }
            }
            catch
            {
                // 没有权限的目录直接跳过
            }
        }

        foreach (var raw in paths)
        {
            if (files.Count >= MaxFilesPerDrop) break;
            if (string.IsNullOrWhiteSpace(raw)) continue;

            try
            {
                if (Directory.Exists(raw)) Walk(raw, 0);
                else if (File.Exists(raw)) AddFile(raw);
            }
            catch
            {
                // 单个路径异常不影响其他
            }
        }

        // 按修改时间升序处理。
        // 一次拖进整个文件夹时，里面往往同时躺着「报告.docx」和「报告_v2.docx」，
        // 枚举顺序取决于文件系统，不排一下的话谁先拿到 v1 是随机的。
        // 按时间排序后，旧文件拿到低版本号，结果稳定且符合直觉。
        files.Sort((a, b) => SafeWriteTime(a).CompareTo(SafeWriteTime(b)));

        return files;
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MaxValue; }
    }
}
