using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using NAudio.CoreAudioApi;

namespace AudioRoute.Services;

/// <summary>
/// Wraps NAudio's CoreAudio APIs:
///   - enumerates render endpoints
///   - listens to plug / unplug events via IMMNotificationClient
///   - changes the default endpoint
///   - reads / writes volume + mute
/// All UI-mutating work is marshalled to the WPF dispatcher.
/// </summary>
public sealed class AudioDeviceService
{
    private MMDeviceEnumerator? _enumerator;
    private NotificationClient? _client;
    private readonly List<MMDevice> _scratch = new();

    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();

    public event Action? DevicesChanged;
    public event Action? DefaultDeviceChanged;
    public event Action? VolumeChanged;
    public event Action<string>? ActionLogged;

    public void Start()
    {
        _enumerator = new MMDeviceEnumerator();
        _client = new NotificationClient();
        _client.DefaultChanged += (flow, role, id) =>
        {
            ActionLogged?.Invoke($"Default device changed → {id}");
            Application.Current?.Dispatcher.Invoke(() => DefaultDeviceChanged?.Invoke());
            Refresh();
        };
        _client.StateChanged += (id, state) =>
        {
            ActionLogged?.Invoke($"Device {id} → {state}");
            Refresh();
        };
        _client.AddedRemoved += () =>
        {
            ActionLogged?.Invoke("Device list changed");
            Refresh();
        };
        _enumerator.RegisterEndpointNotificationCallback(_client);
        Refresh();
    }

    public void Stop()
    {
        try
        {
            if (_enumerator != null && _client != null)
                _enumerator.UnregisterEndpointNotificationCallback(_client);
        }
        catch { /* ignore */ }

        try { _enumerator?.Dispose(); } catch { /* ignore */ }
        DisposeScratch();
        OutputDevices.Clear();
    }

    private void DisposeScratch()
    {
        foreach (var d in _scratch) { try { d.Dispose(); } catch { } }
        _scratch.Clear();
    }

    public void Refresh()
    {
        if (_enumerator == null) return;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            DisposeScratch();
            OutputDevices.Clear();

            try
            {
                var defaultId = GetDefaultId();
                var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);
                _scratch.AddRange(devices);

                foreach (var d in devices)
                {
                    OutputDevices.Add(new AudioDeviceInfo
                    {
                        Id = d.ID,
                        Name = d.FriendlyName ?? "",
                        Description = "",
                        State = d.State,
                        IsDefault = d.ID == defaultId,
                        IconKind = ClassifyIconName(d.FriendlyName ?? ""),
                    });
                }
            }
            catch (Exception ex)
            {
                ActionLogged?.Invoke("Refresh failed: " + ex.Message);
            }

            DevicesChanged?.Invoke();
        });
    }

    public AudioDeviceInfo? GetDefault() =>
        OutputDevices.FirstOrDefault(d => d.IsDefault) ??
        OutputDevices.FirstOrDefault(d => d.IsActive);

    public AudioDeviceInfo? GetById(string id) =>
        OutputDevices.FirstOrDefault(d => d.Id == id);

    public void SetDefault(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return;
        try
        {
            // Multimedia first, then Communications — Windows treats them separately.
            PolicyClient.SetDefaultDevice(deviceId, Role.Multimedia);
            PolicyClient.SetDefaultDevice(deviceId, Role.Communications);
            ActionLogged?.Invoke($"Switched default → {deviceId}");
        }
        catch (Exception ex)
        {
            ActionLogged?.Invoke("SetDefault failed: " + ex.Message);
        }
        Refresh();
    }

    /// <summary>
    /// Cycles the system default to the next active output device.
    /// If <see cref="AppSettings.CycleDeviceIds"/> is non-empty, rotates within
    /// that subset; otherwise uses the full active list.
    /// </summary>
    public void CycleNext()
    {
        var s = App.Settings.Settings;
        var active = OutputDevices.Where(d => d.IsActive).ToList();
        var pool = s.CycleDeviceIds.Count > 0
            ? s.CycleDeviceIds
                  .Select(id => active.FirstOrDefault(d => d.Id == id))
                  .Where(d => d != null)
                  .Cast<AudioDeviceInfo>()
                  .ToList()
            : active;
        if (pool.Count < 2) return;

        var current = pool.FirstOrDefault(d => d.IsDefault) ?? pool[0];
        int idx = pool.IndexOf(current);
        var next = pool[(idx + 1) % pool.Count];
        if (next != null) SetDefault(next.Id);
    }

    public void CyclePrev()
    {
        var s = App.Settings.Settings;
        var active = OutputDevices.Where(d => d.IsActive).ToList();
        var pool = s.CycleDeviceIds.Count > 0
            ? s.CycleDeviceIds
                  .Select(id => active.FirstOrDefault(d => d.Id == id))
                  .Where(d => d != null)
                  .Cast<AudioDeviceInfo>()
                  .ToList()
            : active;
        if (pool.Count < 2) return;

        var current = pool.FirstOrDefault(d => d.IsDefault) ?? pool[0];
        int idx = pool.IndexOf(current);
        var prev = pool[(idx - 1 + pool.Count) % pool.Count];
        if (prev != null) SetDefault(prev.Id);
    }

    public float GetVolumeScalar()
    {
        try
        {
            using var def = _enumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return def?.AudioEndpointVolume?.VolumeScalar ?? 0f;
        }
        catch { return 0f; }
    }

    public bool GetMute()
    {
        try
        {
            using var def = _enumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return def?.AudioEndpointVolume?.Mute ?? false;
        }
        catch { return false; }
    }

    public void SetVolumeScalar(float volume)
    {
        try
        {
            using var def = _enumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (def?.AudioEndpointVolume != null)
                def.AudioEndpointVolume.VolumeScalar = Math.Clamp(volume, 0f, 1f);
            VolumeChanged?.Invoke();
        }
        catch (Exception ex)
        {
            ActionLogged?.Invoke("SetVolume failed: " + ex.Message);
        }
    }

    public void ToggleMute()
    {
        try
        {
            using var def = _enumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (def?.AudioEndpointVolume != null)
                def.AudioEndpointVolume.Mute = !def.AudioEndpointVolume.Mute;
            VolumeChanged?.Invoke();
        }
        catch (Exception ex)
        {
            ActionLogged?.Invoke("ToggleMute failed: " + ex.Message);
        }
    }

    private string GetDefaultId()
    {
        try
        {
            using var def = _enumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return def?.ID ?? "";
        }
        catch { return ""; }
    }

    private static string ClassifyIconName(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("headphone") || lower.Contains("headset") || lower.Contains("earbuds") ||
            lower.Contains("airpods") || lower.Contains("g733") || lower.Contains("g535") || lower.Contains("g930") ||
            lower.Contains("2.4g") || lower.Contains("wireless") || lower.Contains("dongle") ||
            lower.Contains("bluetooth"))
            return "Headphones";
        if (lower.Contains("speaker") || lower.Contains("realtek") || lower.Contains("monitor"))
            return "Speaker";
        return "Device";
    }

    // ----------------------------------------------------------------------
    // NotificationClient — bridges CoreAudio IMMNotificationClient events to
    // plain C# events so the rest of the app stays UI-thread-safe.
    // ----------------------------------------------------------------------
    private sealed class NotificationClient : IMMNotificationClient
    {
        public event Action<DataFlow, Role, string>? DefaultChanged;
        public event Action<string, DeviceState>? StateChanged;
        public event Action? AddedRemoved;

        public void OnDefaultDeviceChanged(DataFlow dataFlow, Role role, string defaultDeviceId)
            => DefaultChanged?.Invoke(dataFlow, role, defaultDeviceId);

        public void OnDeviceAdded(string pwstrDeviceId) => AddedRemoved?.Invoke();
        public void OnDeviceRemoved(string pwstrDeviceId) => AddedRemoved?.Invoke();
        public void OnDeviceStateChanged(string pwstrDeviceId, DeviceState dwNewState)
            => StateChanged?.Invoke(pwstrDeviceId, dwNewState);
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
