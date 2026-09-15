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

        var existing = _store.FindFileByPath(full);
        var chain = _store.VersionChain(groupKey);

        // 情况一：同一个路径已经归档过
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

        if (existing != null)
        {
            // 同一位置、内容变了 —— 这是最典型的"改了再存"场景，直接升版本
            int next = chain.Count == 0 ? existing.Version + 1 : chain.Max(x => x.Version) + 1;
            ApplyVersion(existing, next, hash, info);
            if (entry != null) LinkEntry(existing, entry);

            return new IntakeResult
            {
                Status = IntakeStatus.NewVersion,
                Path = full,
                Record = existing,
                Message = $"{fileName} 内容有更新 → v{next}",
            };
        }

        // 情况二：新路径。先看整条版本链上有没有内容完全一致的版本
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

        // 情况三：真正的新文件 / 新版本
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

        ApplyVersion(record, version, hash, info, isInitial: true);
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
        bool isInitial = false)
    {
        record.Sha256 = hash.Hash;
        record.SizeBytes = info.Length;
        record.LastWriteTime = info.LastWriteTime;
        record.Version = version;
        record.IsMissing = false;

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
        var dataRoot = AppPaths.Root;

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
