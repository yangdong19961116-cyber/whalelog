using System.Collections.Generic;

namespace AudioRoute.Models;

/// <summary>
/// Persistent user preferences. Serialized to
/// %LocalAppData%\AudioRoute\settings.json
/// </summary>
public sealed class AppSettings
{
    /// <summary>The primary 2.4G dongle identifier; matched against device name (case-insensitive, contains).</summary>
    public string TriggerDevicePattern { get; set; } = "";

    /// <summary>Fallback device id used when the trigger device is absent.</summary>
    public string FallbackDeviceId { get; set; } = "";

    /// <summary>If true the app switches automatically when the trigger device is plugged/unplugged.</summary>
    public bool AutoSwitchEnabled { get; set; } = true;

    /// <summary>Optional ordered list of device IDs that participate in the manual cycle.</summary>
    public List<string> CycleDeviceIds { get; set; } = new();

    public string CycleHotkey { get; set; } = "Ctrl+Alt+F11";
    public string NextDeviceHotkey { get; set; } = "Ctrl+Alt+Right";
    public string PrevDeviceHotkey { get; set; } = "Ctrl+Alt+Left";
    public string MuteHotkey { get; set; } = "Ctrl+Alt+M";

    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    public bool FollowSystemTheme { get; set; } = true;
    public AppThemeMode ThemeOverride { get; set; } = AppThemeMode.System;
    public bool UseAcrylicBackdrop { get; set; } = true;

    public int VolumeOnStartup { get; set; } = 70;
    public bool MuteOnStartup { get; set; } = false;
}

public enum AppThemeMode
{
    System,
    Light,
    Dark,
}
