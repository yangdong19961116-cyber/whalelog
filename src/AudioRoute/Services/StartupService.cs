using System.Diagnostics;
using Microsoft.Win32;

namespace AudioRoute.Services;

/// <summary>
/// Manages the per-user "Run on Windows startup" registry key (HKCU\...\Run).
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AudioRoute";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var v = k?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(v);
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enable)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (k == null) return;

            if (enable)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                var args = App.Settings.Settings.StartMinimized ? " --autostart" : "";
                k.SetValue(ValueName, "\"" + exe + "\"" + args, RegistryValueKind.String);
            }
            else
            {
                k.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // best-effort; do not throw out of UI handlers
        }
    }
}
