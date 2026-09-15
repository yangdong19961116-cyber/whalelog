using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WhaleLog.Services;

/// <summary>
/// 开机自启：写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
/// 用 HKCU 不需要提权，卸载/关闭时能干净移除。
/// </summary>
public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WhaleLog";

    public string ExecutablePath
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path)) return path;
            }
            catch
            {
                // 退回主模块路径
            }
            return Process.GetCurrentProcess().MainModule?.FileName ?? "";
        }
    }

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var v = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(v)) return false;

            // 记录里的路径和当前 exe 不一致（比如换过安装位置）时视为未生效
            var recorded = ExtractPath(v);
            return string.Equals(
                Path.GetFullPath(recorded),
                Path.GetFullPath(ExecutablePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public bool Apply(bool enabled)
    {
        try
        {
            var exe = ExecutablePath;
            if (string.IsNullOrWhiteSpace(exe)) return false;

            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null) return false;

            if (enabled)
                key.SetValue(ValueName, $"\"{exe}\" --tray", RegistryValueKind.String);
            else if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractPath(string command)
    {
        var s = command.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            if (end > 0) return s[1..end];
        }

        int argsAt = s.IndexOf(" --", StringComparison.Ordinal);
        return argsAt > 0 ? s[..argsAt].Trim() : s;
    }
}
