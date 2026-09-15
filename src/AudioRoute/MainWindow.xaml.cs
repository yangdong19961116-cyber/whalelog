using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioRoute.Models;
using AudioRoute.Services;

namespace AudioRoute;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _uiTimer;
    private bool _initializing;
    private bool _syncingUi;
    private bool _suppressVolumeApply;

    public MainWindow()
    {
        InitializeComponent();
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _uiTimer.Tick += (_, _) => RefreshStatusBar();
    }

    // -------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initializing || _syncingUi) return;
        _initializing = true;
        try
        {
            // Live services → live UI
            App.Theme.Changed += () => Dispatcher.Invoke(ApplyTheme);
            App.Audio.DevicesChanged += Dispatcher_RefreshDevices;
            App.Audio.DefaultDeviceChanged += Dispatcher_RefreshDevices;
            App.Audio.VolumeChanged += () => Dispatcher.Invoke(SyncVolumeUi);
            App.Audio.ActionLogged += OnActionLogged;
            App.AutoSwitch.ActionLogged += OnActionLogged;
            App.Settings.Changed += Dispatcher_LoadSettingsToUi;

            App.HotKeys.Cycle += OnCycle;
            App.HotKeys.NextDevice += () => Dispatcher.Invoke(() => App.Audio.CycleNext());
            App.HotKeys.PrevDevice += () => Dispatcher.Invoke(() => App.Audio.CyclePrev());
            App.HotKeys.MuteToggle += () => Dispatcher.Invoke(OnMuteToggle);

            ApplyTheme();
            LoadSettingsToUi();
            RefreshDevices();
            SyncVolumeUi();

            App.Audio.OutputDevices.CollectionChanged += OnDevicesCollectionChanged;
            _uiTimer.Start();
        }
        finally
        {
            _initializing = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Hide instead of close — keep the tray app alive.
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    // -------------------------------------------------------------------
    // Theme / DPI
    // -------------------------------------------------------------------

    private void ApplyTheme()
    {
        App.Theme.ApplyToApplication();
        // Volume slider thumb fill follows accent and the brushes are theme-bound,
        // so simply re-applying is enough.
    }

    // -------------------------------------------------------------------
    // Settings → UI
    // -------------------------------------------------------------------

    private void Dispatcher_LoadSettingsToUi()
    {
        if (Dispatcher.CheckAccess()) LoadSettingsToUi();
        else Dispatcher.Invoke(LoadSettingsToUi);
    }

    private void LoadSettingsToUi()
    {
        _syncingUi = true;
        try
        {
            var s = App.Settings.Settings;
            StartWithWindowsCheck.IsChecked = StartupService.IsEnabled();
            StartMinimizedCheck.IsChecked = s.StartMinimized;

            ThemeCombo.SelectedIndex = s.ThemeOverride switch
            {
                AppThemeMode.Light => 1,
                AppThemeMode.Dark => 2,
                _ => 0,
            };

            CycleHotkeyBox.Text = s.CycleHotkey;
            NextHotkeyBox.Text  = s.NextDeviceHotkey;
            PrevHotkeyBox.Text  = s.PrevDeviceHotkey;
            MuteHotkeyBox.Text  = s.MuteHotkey;

            AutoSwitchToggle.IsChecked = s.AutoSwitchEnabled;
            BuildInfo.Text = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0"}  •  .NET {Environment.Version}";
        }
        finally { _syncingUi = false; }
    }

    private void Dispatcher_RefreshDevices()
    {
        if (Dispatcher.CheckAccess()) RefreshDevices();
        else Dispatcher.Invoke(RefreshDevices);
    }

    private void RefreshDevices()
    {
        _syncingUi = true;
        try
        {
            var devices = App.Audio.OutputDevices.ToList();
            var s = App.Settings.Settings;

            // ----- Populate trigger / fallback combos -----
            TriggerCombo.ItemsSource = null;
            TriggerCombo.ItemsSource = devices;
            FallbackCombo.ItemsSource = null;
            FallbackCombo.ItemsSource = devices;

            // Restore selection from settings — _syncingUi guards above prevent feedback.
            var triggerName = s.TriggerDevicePattern ?? "";
            if (!string.IsNullOrWhiteSpace(triggerName) && TriggerCombo.Text != triggerName)
                TriggerCombo.Text = triggerName;

            FallbackCombo.SelectedItem = devices.FirstOrDefault(d => d.Id == s.FallbackDeviceId);

            // ----- Current device card -----
            var current = App.Audio.GetDefault();
            if (current != null)
            {
                CurrentDeviceName.Text = string.IsNullOrWhiteSpace(current.Name) ? "(unnamed device)" : current.Name;
                CurrentDeviceId.Text = current.Id;
                DeviceStateBadge.Text = current.StatusText;
                StatusDot.Fill = current.IsDefault
                    ? (Brush)FindResource("SuccessBrush")
                    : (Brush)FindResource("WarningBrush");
            }
            else
            {
                CurrentDeviceName.Text = "(no output device)";
                CurrentDeviceId.Text = "";
                DeviceStateBadge.Text = "";
            }

            // ----- Status bar -----
            DeviceCountText.Text = devices.Count == 1
                ? "1 device"
                : $"{devices.Count} devices";
        }
        finally { _syncingUi = false; }
    }

    private void RefreshStatusBar()
    {
        var current = App.Audio.GetDefault();
        if (current != null)
        {
            StatusText.Text = current.IsDefault
                ? "Listening"
                : "Switching…";
        }
    }

    private void OnActionLogged(string message)
    {
        Dispatcher.Invoke(() => LastEventText.Text = $"{DateTime.Now:HH:mm:ss}  •  {message}");
    }

    private void SyncVolumeUi()
    {
        _suppressVolumeApply = true;
        try
        {
            var v = App.Audio.GetVolumeScalar();
            int percent = (int)Math.Round(v * 100);
            VolumeSlider.Value = percent;
            VolumeLabel.Text = percent + "%";

            var muted = App.Audio.GetMute();
            MuteIndicatorText.Text = muted ? "Muted" : "";
            MuteButton.Content = muted ? "Unmute" : "Mute";
        }
        finally
        {
            _suppressVolumeApply = false;
        }
    }

    // -------------------------------------------------------------------
    // Button handlers
    // -------------------------------------------------------------------

    private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void PrevDevice_Click(object sender, RoutedEventArgs e) => App.Audio.CyclePrev();
    private void NextDevice_Click(object sender, RoutedEventArgs e) => App.Audio.CycleNext();
    private void Cycle_Click(object sender, RoutedEventArgs e) => OnCycle();

    private void OnCycle()
    {
        Dispatcher.Invoke(() => App.Audio.CycleNext());
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int percent = (int)Math.Round(e.NewValue);
        VolumeLabel.Text = percent + "%";
    }

    private void VolumeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_suppressVolumeApply) return;
        float scalar = (float)(VolumeSlider.Value / 100.0);
        App.Audio.SetVolumeScalar(scalar);
        App.Settings.Update(s => s.VolumeOnStartup = (int)Math.Round(VolumeSlider.Value));
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e) => OnMuteToggle();
    private void OnMuteToggle()
    {
        App.Audio.ToggleMute();
        SyncVolumeUi();
    }

    // -------------------------------------------------------------------
    // Auto-switch
    // -------------------------------------------------------------------

    private void AutoSwitch_Toggle(object sender, RoutedEventArgs e)
    {
        if (_initializing || _syncingUi) return;
        bool enable = AutoSwitchToggle.IsChecked == true;
        App.Settings.Update(s => s.AutoSwitchEnabled = enable);
    }

    private void TriggerCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingUi) return;
        if (TriggerCombo.SelectedItem is AudioDeviceInfo dev && !string.IsNullOrEmpty(dev.Name))
        {
            // User picked from the dropdown — adopt the device's friendly name as the pattern.
            if (TriggerCombo.Text != dev.Name)
                TriggerCombo.Text = dev.Name;
            App.Settings.Update(s => s.TriggerDevicePattern = dev.Name);
        }
    }

    private void TriggerCombo_Loaded(object sender, RoutedEventArgs e)
    {
        // Initialise from settings after ItemsSource is set.
        TriggerCombo.Text = App.Settings.Settings.TriggerDevicePattern ?? "";

        // Hook the inner TextBox so TextChanged fires when user types.
        TriggerCombo.ApplyTemplate();
        if (TriggerCombo.Template?.FindName("PART_EditableTextBox", TriggerCombo) is TextBox inner)
        {
            inner.TextChanged -= OnTriggerTextChanged;
            inner.TextChanged += OnTriggerTextChanged;
        }
    }

    private void OnTriggerTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing || _syncingUi) return;
        var tb = sender as TextBox;
        App.Settings.Update(s => s.TriggerDevicePattern = tb?.Text ?? "");
    }

    private void FallbackCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingUi) return;
        if (FallbackCombo.SelectedItem is AudioDeviceInfo dev)
            App.Settings.Update(s => s.FallbackDeviceId = dev.Id);
    }

    // -------------------------------------------------------------------
    // Hotkeys
    // -------------------------------------------------------------------

    private void HotkeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        // Don't intercept when modifiers-only are pressed (allow clearing).
        if (e.Key == Key.Escape)
        {
            tb.Text = "";
            tb.Tag = null; // signal "no key"
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        bool isModifierKey = e.Key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin;

        if (isModifierKey) return; // wait for the actual key

        string combo = BuildComboString(modifiers, e.Key);
        if (string.IsNullOrEmpty(combo)) return;

        tb.Text = combo;
        e.Handled = true;
        // Move focus away so subsequent misclicks don't rewrite it.
        tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private static string BuildComboString(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        var name = key.ToString();
        // Friendly names
        if (name.StartsWith("Oem")) name = name.Replace("Oem", "");
        if (name.StartsWith("D")) name = name.Replace("D", "");
        parts.Add(name);
        return string.Join("+", parts);
    }

    private void ApplyHotkeys_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings.Settings;
        s.CycleHotkey = CycleHotkeyBox.Text?.Trim() ?? "";
        s.NextDeviceHotkey = NextHotkeyBox.Text?.Trim() ?? "";
        s.PrevDeviceHotkey = PrevHotkeyBox.Text?.Trim() ?? "";
        s.MuteHotkey = MuteHotkeyBox.Text?.Trim() ?? "";
        App.Settings.Save();

        App.HotKeys.ReRegister();
        HotkeyStatus.Text = App.HotKeys.LastError is { } err
            ? "Some hotkeys were not registered: " + err
            : "Hotkeys applied successfully.";
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        CycleHotkeyBox.Text = "Ctrl+Alt+F11";
        NextHotkeyBox.Text  = "Ctrl+Alt+Right";
        PrevHotkeyBox.Text  = "Ctrl+Alt+Left";
        MuteHotkeyBox.Text  = "Ctrl+Alt+M";
    }

    // -------------------------------------------------------------------
    // Settings switches
    // -------------------------------------------------------------------

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _syncingUi) return;
        bool enable = StartWithWindowsCheck.IsChecked == true;
        App.Settings.Update(s => s.StartWithWindows = enable);
        StartupService.SetEnabled(enable);
    }

    private void StartMinimized_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _syncingUi) return;
        bool enable = StartMinimizedCheck.IsChecked == true;
        App.Settings.Update(s => s.StartMinimized = enable);
        // Refresh registry key (depends on StartMinimized flag).
        if (App.Settings.Settings.StartWithWindows) StartupService.SetEnabled(true);
    }

    private void ThemeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingUi) return;
        var mode = ThemeCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag
            ? tag switch
            {
                "Light" => AppThemeMode.Light,
                "Dark" => AppThemeMode.Dark,
                _ => AppThemeMode.System,
            }
            : AppThemeMode.System;

        App.Settings.Update(s => s.ThemeOverride = mode);
        App.Theme.Refresh();
        ApplyTheme();
    }
}
