using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace AudioRoute.Services;

/// <summary>
/// Manages system-wide hotkeys via Win32 RegisterHotKey. Hotkeys are
/// parsed from strings like "Ctrl+Alt+F11" and converted into (modifiers, vk) pairs.
/// </summary>
public sealed class HotKeyService
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 1;
    private const uint MOD_CONTROL = 2;
    private const uint MOD_SHIFT = 4;
    private const uint MOD_WIN = 8;
    private const uint MOD_NOREPEAT = 0x4000;

    private IntPtr _hWnd;
    private HwndSource? _src;
    private int _nextId = 1;
    private readonly Dictionary<int, Action> _callbacks = new();

    public event Action? Cycle;
    public event Action? NextDevice;
    public event Action? PrevDevice;
    public event Action? MuteToggle;

    public string? LastError;

    public void Start()
    {
        var main = Application.Current.MainWindow;
        if (main == null) return;

        var helper = new WindowInteropHelper(main);
        _hWnd = helper.Handle == IntPtr.Zero ? helper.EnsureHandle() : helper.Handle;
        _src = HwndSource.FromHwnd(_hWnd);
        _src.AddHook(WndProc);

        RegisterAll();
    }

    public void Stop()
    {
        try
        {
            foreach (var id in _callbacks.Keys)
                UnregisterHotKey(_hWnd, id);
        }
        catch { /* ignore */ }
        _callbacks.Clear();

        if (_src != null) _src.RemoveHook(WndProc);
    }

    /// <summary>Re-binds hotkeys using the latest settings.</summary>
    public void ReRegister()
    {
        Stop();
        Start();
    }

    private void RegisterAll()
    {
        var s = App.Settings.Settings;
        TryRegister(s.CycleHotkey,    () => Cycle?.Invoke());
        TryRegister(s.NextDeviceHotkey, () => NextDevice?.Invoke());
        TryRegister(s.PrevDeviceHotkey, () => PrevDevice?.Invoke());
        TryRegister(s.MuteHotkey,       () => MuteToggle?.Invoke());
    }

    public bool TryRegister(string combo, Action cb)
    {
        LastError = null;
        if (!ParseCombo(combo, out var mods, out var vk))
        {
            LastError = $"Invalid combo '{combo}'";
            return false;
        }

        int id = _nextId++;
        if (RegisterHotKey(_hWnd, id, mods | MOD_NOREPEAT, vk))
        {
            _callbacks[id] = cb;
            return true;
        }
        LastError = "Hotkey already in use by another application or system shortcut.";
        return false;
    }

    private IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out var cb))
            {
                try { cb(); }
                catch { /* swallow handler exceptions to keep WM_HOTKEY pumping */ }
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Parses strings like "Ctrl+Alt+F11" / "Win+Shift+A" into modifier+VK pairs.
    /// Returns false if the combo has no key or no modifier.
    /// </summary>
    public static bool ParseCombo(string combo, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;

        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                case "ctl":
                    modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win":
                case "meta":
                case "super":
                    modifiers |= MOD_WIN; break;
                default:
                    if (Enum.TryParse<Key>(part, true, out var key))
                    {
                        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
                    }
                    else if (part.StartsWith("F", StringComparison.OrdinalIgnoreCase)
                             && int.TryParse(part.AsSpan(1), out var fNum)
                             && fNum is >= 1 and <= 24)
                    {
                        // F1..F24 — map via VK constants
                        virtualKey = (uint)(0x70 + fNum - 1); // VK_F1 = 0x70
                    }
                    else if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                    {
                        virtualKey = char.ToUpperInvariant(part[0]);
                    }
                    else
                    {
                        return false;
                    }
                    break;
            }
        }

        return modifiers != 0 && virtualKey != 0;
    }
}
