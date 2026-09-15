using System;
using System.Windows;
using AudioRoute.Models;
using Microsoft.Win32;

namespace AudioRoute.Services;

/// <summary>
/// Detects Windows theme via registry (AppsUseLightTheme) and broadcasts changes
/// so the UI can swap resource dictionaries at runtime.
/// </summary>
public sealed class ThemeService
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string LightKey = "AppsUseLightTheme";

    private const string LightUri = "/Resources/Themes/Light.xaml";
    private const string DarkUri = "/Resources/Themes/Dark.xaml";

    public bool IsDark { get; private set; }
    public string ActiveThemeUri => IsDark ? DarkUri : LightUri;

    public event Action? Changed;

    public void Start()
    {
        Refresh();
        SystemEvents.UserPreferenceChanged += OnUserPrefChanged;
    }

    public void Stop()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPrefChanged;
    }

    private void OnUserPrefChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Refresh();
    }

    public void Refresh()
    {
        bool nowDark = ComputeIsDark();
        bool previous = IsDark;
        IsDark = nowDark;
        if (nowDark != previous)
            Changed?.Invoke();
    }

    public void ApplyToApplication()
    {
        var app = Application.Current;
        if (app == null) return;

        var dicts = app.Resources.MergedDictionaries;
        if (dicts.Count == 0) return;

        var current = dicts[0]?.Source?.OriginalString ?? "";
        var desired = ActiveThemeUri;

        if (current.Equals(desired, StringComparison.OrdinalIgnoreCase))
            return;

        var fresh = new ResourceDictionary
        {
            Source = new Uri(desired, UriKind.Relative),
        };
        dicts[0] = fresh;
    }

    private static bool ComputeIsDark()
    {
        var mode = App.Settings.Settings.ThemeOverride;
        if (mode == AppThemeMode.Light) return false;
        if (mode == AppThemeMode.Dark) return true;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            if (key != null)
            {
                var v = key.GetValue(LightKey, 1);
                if (v is int i) return i == 0;
                if (v != null && int.TryParse(v.ToString(), out var p)) return p == 0;
            }
        }
        catch { }
        return false;
    }
}
