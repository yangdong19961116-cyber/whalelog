using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WhaleLog.Services;

/// <summary>
/// 文件指纹与版本链判定。
///
/// 指纹（SHA-256）用来回答一个问题：这个文件我真的改过吗？
/// 没有它，「版本」只能靠文件名猜，用户每拖一次都会多出一个假版本。
///
/// 大文件策略：超过阈值（默认 200MB）不做全量哈希，改成「首尾各 4MB + 均匀采样 16 段」
/// 的组合指纹。这种指纹带上 sample: 前缀，永远不会和全量指纹相等，
/// 所以「采样判定未变化」时不会误判为「已经全量比对过」。
/// </summary>
public sealed class VersionService
{
    private const long SampleBlockSize = 4 * 1024 * 1024;   // 首尾各 4MB
    private const int SampleChunkCount = 16;                // 中间均匀采样段数
    private const int SampleChunkSize = 64 * 1024;          // 每段 64KB
    private const int ReadBufferSize = 1024 * 1024;

    private readonly SettingsService _settings;

    public VersionService(SettingsService settings) => _settings = settings;

    public sealed record HashResult(string Hash, bool Sampled);

    /// <summary>按文件大小自动选择全量或采样哈希。</summary>
    public HashResult ComputeHash(string path)
    {
        long limit = Math.Max(1, _settings.Settings.FullHashLimitMb) * 1024L * 1024L;
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("文件不存在", path);

        return info.Length > limit ? ComputeSampledHash(path, info.Length) : ComputeFullHash(path);
    }

    public static HashResult ComputeFullHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            ReadBufferSize, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var buffer = new byte[ReadBufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            sha.TransformBlock(buffer, 0, read, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return new HashResult(Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant(), false);
    }

    /// <summary>采样指纹：长度 + 首尾段 + 均匀分布的若干段。</summary>
    public static HashResult ComputeSampledHash(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            ReadBufferSize, FileOptions.RandomAccess);
        using var sha = SHA256.Create();

        void Feed(byte[] data)
            => sha.TransformBlock(data, 0, data.Length, null, 0);

        // 把文件长度本身也混进去，长度不同的文件不可能得到同一指纹
        Feed(BitConverter.GetBytes(length));

        var head = ReadAt(stream, 0, (int)Math.Min(SampleBlockSize, length));
        Feed(head);

        if (length > SampleBlockSize)
        {
            long tailStart = Math.Max(0, length - SampleBlockSize);
            var tail = ReadAt(stream, tailStart, (int)(length - tailStart));
            Feed(tail);
        }

        if (length > SampleBlockSize * 2 && SampleChunkCount > 0)
        {
            long usable = length - SampleBlockSize * 2;
            for (int i = 0; i < SampleChunkCount; i++)
            {
                long offset = SampleBlockSize + (usable * i / SampleChunkCount);
                var chunk = ReadAt(stream, offset, SampleChunkSize);
                if (chunk.Length > 0) Feed(chunk);
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hex = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
        return new HashResult("sample:" + hex, true);
    }

    private static byte[] ReadAt(FileStream stream, long offset, int count)
    {
        if (count <= 0) return Array.Empty<byte>();
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);
            if (read <= 0) break;
            total += read;
        }
        return total == count ? buffer : buffer[..total];
    }

    // ---------------------------------------------------------------
    // 版本链分组键
    // ---------------------------------------------------------------

    private static readonly Regex[] SuffixPatterns =
    {
        // (1) （2）
        new(@"[\s\-_]*[（(]\s*\d+\s*[)）]\s*$", RegexOptions.Compiled),
        // 副本 / 复件 / copy / 复制
        new(@"[\s\-_]*(副本|复件|复制|copy)\s*\d*\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // 第 2 版 / 3 稿 / 修订 2
        new(@"[\s\-_.]*(第\s*)?\d+\s*(版|稿|修订)\s*$", RegexOptions.Compiled),
        // v2 / v1.3 / ver2 / version 2
        new(@"[\s\-_.]*v(?:er|ersion)?\s*\d+(?:\.\d+)*\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // final / 定稿 / 最终 / 终极
        new(@"[\s\-_.]*(final|finale|定稿|最终|终极)\s*\d*\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    /// <summary>去掉主文件名里的版本痕迹，得到版本链分组基名。</summary>
    public static string NormalizeBaseName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(name)) return "";

        // 反复剥离，「报告 v2 (1)」这种叠加后缀要一次剥干净
        for (int pass = 0; pass < 4; pass++)
        {
            var before = name;
            foreach (var rx in SuffixPatterns)
                name = rx.Replace(name, "");
            if (name.Equals(before, StringComparison.Ordinal)) break;
        }

        name = name.Trim().TrimEnd('-', '_', '.', ' ', '·');
        return name.Length == 0 ? Path.GetFileNameWithoutExtension(fileName).Trim() : name;
    }

    /// <summary>
    /// 版本链分组键 = 归一化基名 + 扩展名（都小写）。
    /// 带扩展名是为了避免「报告.docx」和「报告.pdf」被串成一条链 —— 它们通常是
    /// 同一份交付物的不同形态，硬串在一起反而干扰阅读。
    /// </summary>
    public static string BuildVersionGroupKey(string fileName)
    {
        var baseName = NormalizeBaseName(fileName).ToLowerInvariant();
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return baseName + "|" + ext;
    }

    /// <summary>把分组键变成可读的展示名。</summary>
    public static string GroupKeyDisplay(string key)
    {
        int bar = key.LastIndexOf('|');
        return bar < 0 ? key : key[..bar] + key[(bar + 1)..];
    }

    /// <summary>判断某个主文件名里是否带有显式版本标记，用于 UI 提示。</summary>
    public static bool HasExplicitVersionMarker(string fileName)
        => !string.Equals(
            NormalizeBaseName(fileName),
            Path.GetFileNameWithoutExtension(fileName),
            StringComparison.Ordinal);

    public static string SanitizeForPath(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        var s = sb.ToString().Trim();
        return s.Length > 60 ? s[..60] : s;
    }
}
