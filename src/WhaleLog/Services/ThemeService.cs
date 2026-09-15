using System.Windows;
using Microsoft.Win32;
using WhaleLog.Models;

namespace WhaleLog.Services;

/// <summary>
/// 浅色 / 深色主题切换。读取注册表 AppsUseLightTheme 判断系统当前主题，
/// 系统主题变化时广播 Changed 让界面换字典。
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
        {
            ApplyToApplication();
            Changed?.Invoke();
        }
    }

    /// <summary>强制按当前设置重算并应用（用户改完主题选项后调用）。</summary>
    public void ForceApply()
    {
        bool nowDark = ComputeIsDark();
        bool changed = nowDark != IsDark;
        IsDark = nowDark;
        ApplyToApplication();
        if (changed) Changed?.Invoke();
    }

    public void ApplyToApplication()
    {
        var app = Application.Current;
        if (app == null) return;

        var dicts = app.Resources.MergedDictionaries;
        if (dicts.Count == 0) return;

        // 只认 Light.xaml / Dark.xaml 这两个字典，别把 Shared.xaml 之类的样式字典换掉
        int themeIndex = -1;
        for (int i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i]?.Source?.OriginalString ?? "";
            if (src.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                src.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase))
            {
                themeIndex = i;
                break;
            }
        }
        if (themeIndex < 0) return;

        var desired = ActiveThemeUri;
        var current = dicts[themeIndex]?.Source?.OriginalString ?? "";
        if (current.Equals(desired, StringComparison.OrdinalIgnoreCase))
            return;

        dicts[themeIndex] = new ResourceDictionary { Source = new Uri(desired, UriKind.Relative) };
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
        catch
        {
            // 读不到按浅色处理
        }
        return false;
    }
}
