using System.IO;

namespace WhaleLog.Services;

/// <summary>
/// 应用所有落盘位置的唯一来源。
/// 便携模式：可执行文件同目录存在 portable.txt 时，数据放在程序目录下的 data\。
/// 默认模式：%AppData%\WhaleLog\。
/// </summary>
public static class AppPaths
{
    private static string? _root;

    public static string Root => _root ??= ResolveRoot();

    /// <summary>每日笔记（Markdown）目录。</summary>
    public static string NotesDir => Path.Combine(Root, "notes");

    /// <summary>归档快照目录（仅在开启快照模式时使用）。</summary>
    public static string ArchiveDir => Path.Combine(Root, "archive");

    /// <summary>自动生成的日报 / 周报。</summary>
    public static string ReportsDir => Path.Combine(Root, "reports");

    public static string IndexFile => Path.Combine(Root, "index.json");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string RulesFile => Path.Combine(Root, "rules.json");

    public static string LogDir => Path.Combine(Root, "logs");

    /// <summary>
    /// 外部素材目录：程序旁边的 Assets\。
    ///
    /// 刻意不放在数据目录里 —— 素材是跟着**程序**走的（分发时一起打包、
    /// 换台机器也该是同一套形象），数据目录装的是"这台机器上你的记录"。
    ///
    /// 可以用环境变量 WHALELOG_ASSET_DIR 指向别处：隔离测试用，
    /// 也方便把素材集中放在一个共享目录里。
    /// </summary>
    public static string AssetsDir
    {
        get
        {
            try
            {
                var overridden = Environment.GetEnvironmentVariable("WHALELOG_ASSET_DIR");
                if (!string.IsNullOrWhiteSpace(overridden))
                    return Path.GetFullPath(overridden);

                return Path.Combine(AppContext.BaseDirectory, "Assets");
            }
            catch
            {
                return "Assets";
            }
        }
    }

    private static string ResolveRoot()
    {
        // 最高优先级：环境变量指定。自动化测试和"数据放哪儿我自己定"的场景用得上。
        var overridden = Environment.GetEnvironmentVariable("WHALELOG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            try { return Path.GetFullPath(overridden); }
            catch { /* 路径非法就往下走默认逻辑 */ }
        }

        try
        {
            var exeDir = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(exeDir, "portable.txt")))
                return Path.Combine(exeDir, "data");
        }
        catch
        {
            // 取不到就退回漫游目录
        }

        return Path.Combine(GetRoamingAppData(), "WhaleLog");
    }

    /// <summary>
    /// 取漫游 AppData 目录。
    ///
    /// Environment.GetFolderPath 在个别受限环境（沙箱、被裁剪过的服务会话）会返回
    /// 空值甚至 null，直接 Path.Combine 会抛异常。这里逐级回退，保证程序一定能启动 ——
    /// 数据存哪儿是次要问题，起不来才是。
    /// </summary>
    private static string GetRoamingAppData()
    {
        var candidates = new[]
        {
            SafeGetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetEnvironmentVariable("APPDATA") ?? "",
            Combine(SafeGetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming"),
            Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "AppData", "Roaming"),
            AppContext.BaseDirectory,
        };

        foreach (var path in candidates)
        {
            if (!string.IsNullOrWhiteSpace(path) && SafeIsPathLike(path))
                return path;
        }

        return AppContext.BaseDirectory;
    }

    private static string SafeGetFolderPath(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder) ?? ""; }
        catch { return ""; }
    }

    private static string Combine(params string[] parts)
    {
        var usable = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return usable.Length switch
        {
            0 => "",
            1 => usable[0],
            _ => Path.Combine(usable),
        };
    }

    /// <summary>排除掉明显不是目录的候选值（比如被误设成单个字符的环境变量）。</summary>
    private static bool SafeIsPathLike(string path)
    {
        try
        {
            return Path.IsPathRooted(path) && path.Length > 3;
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(NotesDir);
        Directory.CreateDirectory(LogDir);
    }

    /// <summary>把路径转成便于双击打开的形式。</summary>
    public static string ToDayFileName(DateTime date) => $"{date:yyyy-MM-dd}.md";

    public static string ToReportFileName(DateTime date) => $"{date:yyyy-MM-dd}-日报.md";

    public static string ToWeeklyReportFileName(DateTime weekStart) => $"{weekStart:yyyy-MM-dd}-周报.md";
}
