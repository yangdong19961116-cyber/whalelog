using System.IO;
using System.Reflection;

namespace WhaleLog.Services;

/// <summary>
/// 桌面快捷方式的创建与更新。
///
/// 通过 WScript.Shell 这个 COM 组件创建 .lnk（Windows 上最省事也最可靠的方式），
/// 用反射做后期绑定，省掉一个 Interop 程序集依赖。
///
/// 用 `SpecialFolders("Desktop")` 而不是拼 `%USERPROFILE%\Desktop`：
/// 桌面被 OneDrive 重定向或换了系统语言时，只有它拿得到真实路径。
/// </summary>
public static class ShortcutService
{
    public const string ShortcutName = "鲸记 WhaleLog.lnk";

    public sealed record LaunchTarget(string Target, string Arguments, string WorkingDirectory, bool IsDotnetMuxer);

    /// <summary>
    /// 算出"双击该用什么启动我"。
    ///
    /// 分两种情况：直接用 WhaleLog.exe 启动（apphost 或发布版），
    /// 或者被 `dotnet WhaleLog.dll` 宿主启动。前者拉自己的 exe，后者得拉 dotnet.exe 并把 dll 当参数。
    /// </summary>
    public static LaunchTarget ResolveLaunchTarget()
    {
        var processPath = Environment.ProcessPath ?? "";
        var assemblyPath = Assembly.GetEntryAssembly()?.Location ?? "";

        bool viaMuxer = string.IsNullOrEmpty(processPath)
                        || Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);

        if (viaMuxer && !string.IsNullOrEmpty(assemblyPath))
        {
            return new LaunchTarget(
                processPath,
                $"\"{assemblyPath}\"",
                Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory,
                true);
        }

        if (!string.IsNullOrEmpty(processPath))
        {
            return new LaunchTarget(
                processPath,
                "",
                Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory,
                false);
        }

        return new LaunchTarget("", "", AppContext.BaseDirectory, false);
    }

    public static string DesktopDirectory
    {
        get
        {
            var shell = TryCreateShell();
            if (shell != null)
            {
                try
                {
                    var folders = shell.GetType().InvokeMember(
                        "SpecialFolders", BindingFlags.GetProperty, null, shell, null);

                    var desktop = folders?.GetType().InvokeMember(
                        "Item", BindingFlags.GetProperty, null, folders, new object[] { "Desktop" }) as string;

                    if (!string.IsNullOrWhiteSpace(desktop)) return desktop;
                }
                catch
                {
                    // 落到下面的兜底路径
                }
            }

            var profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            return string.IsNullOrWhiteSpace(profile)
                ? AppContext.BaseDirectory
                : Path.Combine(profile, "Desktop");
        }
    }

    public static string ShortcutPath => Path.Combine(DesktopDirectory, ShortcutName);

    public static bool Exists() => File.Exists(ShortcutPath);

    /// <summary>创建或覆盖桌面快捷方式。</summary>
    public static bool TryCreate(out string message)
        => TryCreateAt(ShortcutPath, out message);

    /// <summary>
    /// 在指定路径创建快捷方式。
    ///
    /// 诊断自检走这个重载、并且打到临时目录 —— 早期版本的自检直接调上面那个，
    /// 结果"从解压出来的副本跑一次自检"就把用户桌面的快捷方式改指到了那个临时目录，
    /// 临时副本一删，快捷方式就废了。验证机制不该动用户的桌面。
    /// </summary>
    public static bool TryCreateAt(string linkPath, out string message)
    {
        try
        {
            var launch = ResolveLaunchTarget();
            if (string.IsNullOrEmpty(launch.Target))
            {
                message = "定位不到程序自身，无法创建快捷方式";
                return false;
            }

            var dir = Path.GetDirectoryName(linkPath) ?? "";
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                message = $"目录不存在：{dir}";
                return false;
            }

            var shell = TryCreateShell();
            if (shell == null)
            {
                message = "系统组件 WScript.Shell 不可用，无法创建快捷方式";
                return false;
            }

            var shortcut = shell.GetType().InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
            if (shortcut == null)
            {
                message = "创建快捷方式失败";
                return false;
            }

            var type = shortcut.GetType();
            void Set(string name, string value)
                => type.InvokeMember(name, BindingFlags.SetProperty, null, shortcut, new object[] { value });

            Set("TargetPath", launch.Target);
            Set("WorkingDirectory", launch.WorkingDirectory);
            Set("Description", "鲸记 WhaleLog · 每日工作记录与文件归档");

            if (!string.IsNullOrEmpty(launch.Arguments))
                Set("Arguments", launch.Arguments);

            // 图标优先用真实存在的 .ico 文件；拿不到就退回可执行文件自身的图标
            var icon = IconAssets.EnsureIconFile();
            Set("IconLocation", !string.IsNullOrEmpty(icon)
                ? $"{icon},0"
                : $"{launch.Target},0");

            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

            // 顺手清掉可能按旧路径留下的快捷方式
            CleanupStale(dir, linkPath);

            message = $"{linkPath}（指向 {launch.Target}）";
            return true;
        }
        catch (Exception ex)
        {
            message = $"创建快捷方式失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>读出一个 .lnk 实际指向的可执行文件，读不到返回 null。</summary>
    public static string? TryReadTarget(string linkPath)
    {
        try
        {
            if (!File.Exists(linkPath)) return null;

            var shell = TryCreateShell();
            if (shell == null) return null;

            var shortcut = shell.GetType().InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
            if (shortcut == null) return null;

            return shortcut.GetType().InvokeMember(
                "TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>删除旧的 dotnet.exe 快捷方式（首次版本可能叫别的名字）。</summary>
    private static void CleanupStale(string desktop, string keepPath)
    {
        try
        {
            foreach (var candidate in new[] { "WhaleLog.lnk", "鲸记.lnk" })
            {
                var path = Path.Combine(desktop, candidate);
                if (!string.Equals(path, keepPath, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    File.Delete(path);
            }
        }
        catch
        {
            // 清理失败无所谓
        }
    }

    private static object? TryCreateShell()
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            return type == null ? null : Activator.CreateInstance(type);
        }
        catch
        {
            return null;
        }
    }
}
