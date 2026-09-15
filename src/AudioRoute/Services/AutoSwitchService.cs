using System;
using System.Linq;
using System.Timers;
using NAudio.CoreAudioApi;

namespace AudioRoute.Services;

/// <summary>
/// Subscribes to device change events and applies the trigger/fallback rules.
/// - When trigger device becomes Active → switch default to it.
/// - When trigger device is Unplugged → switch default to fallback id.
/// All decisions are debounced and rate-limited so a flaky dongle
/// doesn't thrash the default endpoint.
/// </summary>
public sealed class AutoSwitchService
{
    private Timer? _debounce;
    private string? _pendingTarget;
    private DateTime _lastSwitchAt = DateTime.MinValue;
    private static readonly TimeSpan MinSwitchInterval = TimeSpan.FromMilliseconds(800);

    public event Action<string>? ActionLogged;

    public void Start()
    {
        App.Audio.DevicesChanged += OnDevicesChanged;
        App.Settings.Changed += () => { /* settings changed — next event will re-evaluate */ };
    }

    public void Stop()
    {
        App.Audio.DevicesChanged -= OnDevicesChanged;
        App.Audio.DefaultDeviceChanged -= null!;
        try { _debounce?.Stop(); _debounce?.Dispose(); } catch { /* ignore */ }
    }

    private void OnDevicesChanged()
    {
        if (!App.Settings.Settings.AutoSwitchEnabled) return;
        var pattern = App.Settings.Settings.TriggerDevicePattern ?? "";
        if (string.IsNullOrWhiteSpace(pattern)) return;

        var trigger = App.Audio.OutputDevices
            .FirstOrDefault(d => d.IsActive && Contains(d.Name, pattern));
        var fallbackId = App.Settings.Settings.FallbackDeviceId;

        string targetId;
        if (trigger != null && !string.IsNullOrEmpty(trigger.Id))
        {
            targetId = trigger.Id;
            ActionLogged?.Invoke($"AutoSwitch: trigger matched ({trigger.Name})");
        }
        else if (!string.IsNullOrEmpty(fallbackId))
        {
            // Only fall back if the current default isn't already the trigger
            // (avoids fighting with hot-plug bounces on close).
            targetId = fallbackId;
            ActionLogged?.Invoke("AutoSwitch: trigger absent → fallback");
        }
        else
        {
            return;
        }

        var current = App.Audio.GetDefault()?.Id;
        if (string.Equals(current, targetId, StringComparison.OrdinalIgnoreCase)) return;
        if (DateTime.UtcNow - _lastSwitchAt < MinSwitchInterval) return;

        Schedule(targetId, App.Settings.Settings.AutoSwitchDebounceMs > 0 ? App.Settings.Settings.AutoSwitchDebounceMs : 1500);
    }

    private void Schedule(string targetId, int delayMs)
    {
        _pendingTarget = targetId;
        try { _debounce?.Stop(); _debounce?.Dispose(); } catch { /* ignore */ }

        _debounce = new Timer(delayMs) { AutoReset = false };
        _debounce.Elapsed += (_, _) =>
        {
            var t = _pendingTarget;
            _pendingTarget = null;
            if (string.IsNullOrEmpty(t)) return;
            App.Audio.SetDefault(t);
            _lastSwitchAt = DateTime.UtcNow;
            ActionLogged?.Invoke($"AutoSwitch: applied default → {t}");
        };
        _debounce.Start();
    }

    private static bool Contains(string? name, string pattern) =>
        name != null && name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
}
