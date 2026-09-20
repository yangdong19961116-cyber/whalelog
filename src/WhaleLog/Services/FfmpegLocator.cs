using System.Diagnostics;
using System.IO;

namespace WhaleLog.Services;

/// <summary>
/// 找 ffmpeg.exe。透明 webm 的动画要靠它解码（WPF 自己解不了 VP9 的 alpha 通道）。
///
/// 为什么不把 ffmpeg 静态链进程序集
/// ------------------------------
/// 那个 exe 有 88 MB，链进来之后程序本体就从 58 MB 涨到 146 MB，
/// 而且每次改一行 C# 都要重新链接一遍。它是**外部工具**，不是代码依赖 ——
/// 放在程序旁边、按需调用最合适，找不到也只是"视频动画播不了"，
/// 静态形象和精灵图动画照常。
///
/// 查找顺序（前两个是我们要铺的，后两个是兜底）：
///   1. 程序目录\ffmpeg.exe            —— 便携包解压出来就是这一层
///   2. 素材目录\ffmpeg.exe            —— 有人喜欢把素材和工具堆一起
///   3. 程序目录\tools\ffmpeg.exe
///   4. PATH 里的 ffmpeg              —— 开发机上自己装过一个
///
/// 这个类不碰 WPF，自测能直接编译它。
/// </summary>
public static class FfmpegLocator
{
    private static bool _searched;
    private static string? _path;
    private static string? _version;
    private static string _where = "";

    /// <summary>ffmpeg 的完整路径，找不到返回 null。结果会缓存。</summary>
    public static string? Path
    {
        get
        {
            if (!_searched) Search();
            return _path;
        }
    }

    /// <summary>能不能播视频动作。</summary>
    public static bool IsAvailable => Path != null;

    /// <summary>找不到时给用户看的原因（说明到底找过哪几个地方）。</summary>
    public static string Where => _where;

    /// <summary>ffmpeg 的版本号（第一行），取不到返回空串。会真的起一次进程，所以是惰性的。</summary>
    public static string Version
    {
        get
        {
            if (_version != null) return _version;
            if (Path is not { } exe) return _version = "";

            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "-version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (p == null) return _version = "";

                var line = p.StandardOutput.ReadLine() ?? "";
                p.WaitForExit(4000);
                return _version = line.Trim();
            }
            catch
            {
                return _version = "";
            }
        }
    }

    /// <summary>丢掉缓存重新找。「重新加载素材」会用它。</summary>
    public static void Reset()
    {
        _searched = false;
        _path = null;
        _version = null;
        _where = "";
    }

    private static void Search()
    {
        _searched = true;

        var tried = new List<string>();

        foreach (var candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            tried.Add(candidate);

            try
            {
                if (File.Exists(candidate))
                {
                    _path = candidate;
                    _where = $"已找到：{candidate}";
                    return;
                }
            }
            catch
            {
                // 路径非法就试下一个
            }
        }

        // 最后看一眼 PATH
        try
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(System.IO.Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var full = System.IO.Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                    if (File.Exists(full))
                    {
                        _path = full;
                        _where = $"已找到（来自 PATH）：{full}";
                        return;
                    }
                }
                catch { /* 单个目录非法不影响其它 */ }
            }
        }
        catch { /* PATH 读不到就算了 */ }

        _path = null;
        _where = "没找到 ffmpeg.exe —— 视频动作播不了（形象和精灵图动画不受影响）。找过：" +
                 string.Join("、", tried.Take(3)) + " 以及 PATH";
    }

    private static IEnumerable<string> Candidates()
    {
        string baseDir;
        try { baseDir = AppContext.BaseDirectory; }
        catch { baseDir = ""; }

        if (baseDir.Length > 0)
        {
            yield return System.IO.Path.Combine(baseDir, "ffmpeg.exe");
            yield return System.IO.Path.Combine(baseDir, "tools", "ffmpeg.exe");
        }

        string assets;
        try { assets = AppPaths.AssetsDir; }
        catch { assets = ""; }

        if (assets.Length > 0)
        {
            yield return System.IO.Path.Combine(assets, "ffmpeg.exe");
            yield return System.IO.Path.Combine(assets, "tools", "ffmpeg.exe");
        }
    }

    /// <summary>诊断报告里的一行。</summary>
    public static string Describe()
        => IsAvailable
            ? $"ffmpeg：{_path}（{Version}）"
            : $"ffmpeg：缺失。{_where}";
}
