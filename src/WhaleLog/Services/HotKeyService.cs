using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace WhaleLog.Services;

/// <summary>
/// 系统级全局快捷键（Win32 RegisterHotKey）。
///
/// 用一个**消息专用窗口**（HWND_MESSAGE 的父窗口）来接收 WM_HOTKEY，
/// 而不是挂在主窗口上 —— 鲸记可以从托盘静默启动（不创建主窗口），
/// 挂在主窗口上会导致那种情况下快捷键注册不了。
/// </summary>
public sealed class HotKeyService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const int HWND_MESSAGE = -3;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly Dictionary<int, Action> _callbacks = new();

    private HwndSource? _source;
    private int _nextId = 1;
    private bool _disposed;

    /// <summary>最近一次注册失败的原因，界面拿它提示用户。</summary>
    public string? LastError { get; private set; }

    public bool HasAnyRegistration => _callbacks.Count > 0;

    public void Start()
    {
        if (_source != null) return;

        var parameters = new HwndSourceParameters("WhaleLogHotKeySink")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(HWND_MESSAGE),
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public void Stop()
    {
        if (_source == null) return;

        foreach (var id in _callbacks.Keys.ToList())
        {
            try { UnregisterHotKey(_source.Handle, id); }
            catch { /* 注销失败不影响退出 */ }
        }
        _callbacks.Clear();

        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }

    /// <summary>用最新设置重新绑定（用户改了快捷键之后调用）。</summary>
    public void ReRegister(string? combo, Action callback)
    {
        Stop();
        Start();

        if (string.IsNullOrWhiteSpace(combo)) return;
        TryRegister(combo, callback);
    }

    /// <summary>注册一个组合键。返回 false 时 LastError 里有原因（大多是被别的程序占了）。</summary>
    public bool TryRegister(string combo, Action callback)
    {
        LastError = null;

        if (_source == null) Start();
        if (_source == null)
        {
            LastError = "消息窗口创建失败";
            return false;
        }

        if (!TryParse(combo, out var modifiers, out var virtualKey))
        {
            LastError = $"无法识别的组合键：{combo}";
            return false;
        }

        int id = _nextId++;
        if (RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, virtualKey))
        {
            _callbacks[id] = callback;
            return true;
        }

        LastError = $"{Format(modifiers, virtualKey)} 已被其他程序或系统占用，换一个组合试试";
        return false;
    }

    private IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            handled = true;
            try { callback(); }
            catch
            {
                // 处理器抛异常不能影响 WM_HOTKEY 消息循环，否则后续快捷键全部失效
            }
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------
    // 组合键解析与显示
    // ------------------------------------------------------------------

    /// <summary>把 "Ctrl+Alt+N" 这类字符串解析成 (修饰键, 虚拟键码)。</summary>
    public static bool TryParse(string combo, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;

        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                case "ctl":
                    modifiers |= MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= MOD_ALT;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                case "win":
                case "meta":
                case "super":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    if (!TryParseKey(raw, out virtualKey)) return false;
                    break;
            }
        }

        // 裸按键会抢占全局输入，必须带至少一个修饰键
        return modifiers != 0 && virtualKey != 0;
    }

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;

        if (token.StartsWith('F') || token.StartsWith('f'))
        {
            if (int.TryParse(token.AsSpan(1), out var f) && f is >= 1 and <= 24)
            {
                virtualKey = (uint)(0x70 + f - 1);   // VK_F1 = 0x70
                return true;
            }
        }

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                virtualKey = c;
                return true;
            }
        }

        if (Enum.TryParse<Key>(token, true, out var key))
        {
            var vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk != 0)
            {
                virtualKey = (uint)vk;
                return true;
            }
        }

        return false;
    }

    /// <summary>把修饰键 + 虚拟键码还原成可读文本。</summary>
    public static string Format(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>(4);
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(FormatKey(virtualKey));
        return string.Join("+", parts);
    }

    private static string FormatKey(uint virtualKey)
    {
        if (virtualKey is >= 0x70 and <= 0x87) return $"F{virtualKey - 0x70 + 1}";
        if (virtualKey is >= 'A' and <= 'Z') return ((char)virtualKey).ToString();
        if (virtualKey is >= '0' and <= '9') return ((char)virtualKey).ToString();

        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        return key == Key.None ? $"VK{virtualKey:X2}" : key.ToString();
    }

    /// <summary>把当前按键事件转成组合键字符串，给"录制快捷键"用。未按修饰键时返回 null。</summary>
    public static string? FromKeyEvent(Key key, ModifierKeys modifiers)
    {
        uint mods = 0;
        if ((modifiers & ModifierKeys.Control) != 0) mods |= MOD_CONTROL;
        if ((modifiers & ModifierKeys.Alt) != 0) mods |= MOD_ALT;
        if ((modifiers & ModifierKeys.Shift) != 0) mods |= MOD_SHIFT;
        if ((modifiers & ModifierKeys.Windows) != 0) mods |= MOD_WIN;

        if (mods == 0) return null;

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return null;

        // 只按下修饰键本身不算
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None)
        {
            return null;
        }

        return Format(mods, (uint)vk);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
