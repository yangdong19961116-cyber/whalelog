# Audio Route

> A lightweight tray-resident Windows 10 / 11 utility that switches the default
> audio output device with global hotkeys, and **auto-switches to your 2.4G
> wireless dongle the moment it pairs up** — then falls back to your desktop
> speakers when you walk away.

![tech](https://img.shields.io/badge/.NET-8-blueviolet) ![tech](https://img.shields.io/badge/WPF-native-lightgrey) ![tech](https://img.shields.io/badge/OS-Win10%20%2F%20Win11-0078D4)

---

## Features

- **Global hotkeys**
  - Cycle (default `Ctrl+Alt+F11`) — rotates through every active output.
  - Next device (`Ctrl+Alt+Right`) / Previous device (`Ctrl+Alt+Left`).
  - Mute toggle (`Ctrl+Alt+M`).
  - All four hotkeys are captured by an interactive "press any key" editor in
    the UI, validated, and re-registered live.
- **Auto-switch on device events**
  - Pick a substring (e.g. `2.4G`, `G733`, `HyperX`) that matches the
    dongle's friendly name. When any device-added / state-changed event
    fires from `IMMNotificationClient`, the helper re-evaluates and switches.
  - Pick a *fallback* device — the moment the trigger disappears, output
    jumps back to your speakers / DAC.
  - 800 ms rate-limit and a configurable debounce keep a flaky dongle from
    thrashing the default endpoint.
- **Modern, themeable UI**
  - Two visual states (light + dark) implemented as `ResourceDictionary`
    files at `src/AudioRoute/Resources/Themes/`.
  - Auto-follows `HKCU\…\Themes\Personalize\AppsUseLightTheme` (live
    `SystemEvents.UserPreferenceChanged`) and supports per-app override.
  - Responsive 2 × 2 card grid with scroll fallback, DPI-per-monitor aware,
    clean typography.
- **Tray-resident**
  - Closing the window hides to the system tray. Right-click for "Cycle next
    output" or "Exit".
  - "Start with Windows" / "Start minimized" toggles via the standard
    `HKCU\…\Run` registry key.
- **Self-contained single-file build** — `AudioRoute.exe` runs on a stock
  Windows 10 box with no .NET runtime installed (publishes as
  `PublishSingleFile=true SelfContained=true`).

---

## Quick start

### 1. Build it (Windows 10 / 11)

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
once. Then from the repo root:

```cmd
build.cmd
```

This produces a fully self-contained single-file binary at:

```
.\Publish\AudioRoute.exe
```

You can copy that single `.exe` anywhere — no installer, no runtime, no
side-by-side files.

To build with Visual Studio: open `AudioRoute.sln` and press F5.

### 2. Use it

1. Launch `AudioRoute.exe`.
2. Under **Auto-switch on 2.4G dongle**:
   - Toggle on.
   - Type or pick your dongle's friendly name pattern (anything containing
     it counts — e.g. `2.4G`, `G733`, `HyperX`).
   - Pick a *Fallback device* for when the dongle goes to sleep.
3. Plug/unplug the dongle — output should snap to it within ~1 s.
4. Use `Ctrl+Alt+F11` (or whatever you mapped Cycle to) to hand-cycle
   between speakers, dongle, headset, etc.

Settings persist at `%LocalAppData%\AudioRoute\settings.json`.
Logs (only on crash) at `%LocalAppData%\AudioRoute\logs\`.

---

## Tech stack

| Layer | Choice |
|---|---|
| UI | WPF on .NET 8 (Windows) with custom-themed `ResourceDictionary` |
| Audio | [NAudio 2.2](https://github.com/naudio/NAudio) — `MMDeviceEnumerator`, `IMMNotificationClient`, `PolicyClient` |
| Hotkeys | Win32 `RegisterHotKey` via `HwndSource.AddHook(WM_HOTKEY)` |
| Theme detection | `Registry` + `SystemEvents.UserPreferenceChanged` |
| Persistence | `System.Text.Json` → `%LocalAppData%` |
| Autostart | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |
| Single-file publish | `PublishSingleFile=true SelfContained=true IncludeNativeLibrariesForSelfExtract=true` |

### Architecture

```
App.xaml(.cs)
 ├── SettingsService        – load/save + change events
 ├── ThemeService           – registry + Manual + dynamic theme swap
 ├── AudioDeviceService     – NAudio wrapper; plug/unplug + volume + default
 ├── HotKeyService          – RegisterHotKey + combo parser
 └── AutoSwitchService      – subscribes to device events, applies rules

MainWindow.xaml(.cs)        – UI; closes only hide-to-tray
```

`AudioRoute.exe` is a single-instance tray app: closing the window hides to
the tray. `App.OnExit` unwinds services cleanly.

---

## Hotkey reference

| Action | Default | Notes |
|---|---|---|
| Cycle playback | `Ctrl+Alt+F11` | Rotates through *active* outputs |
| Next device | `Ctrl+Alt+Right` | Skip-style next |
| Previous device | `Ctrl+Alt+Left` | |
| Mute toggle | `Ctrl+Alt+M` | Toggles the default endpoint's mute |

Use any combo the Win32 `RegisterHotKey` accepts, e.g. `Ctrl+Shift+F12`,
`Win+Alt+Y`. Modifier-less or already-system-bound shortcuts (e.g.
`Win+L`) will be refused at apply time.

---

## Project layout

```
AudioRoute/
├── AudioRoute.sln
├── build.cmd                              one-shot build + publish
├── LICENSE                                MIT
├── README.md                              you are here
├── .gitignore
└── src/
    └── AudioRoute/
        ├── AudioRoute.csproj              .NET 8 Windows, NAudio 2.2
        ├── App.xaml(.cs)                  entry point, tray + autostart
        ├── MainWindow.xaml(.cs)           UI
        ├── app.manifest                   asInvoker + DPI per-monitor
        ├── AssemblyInfo.cs
        ├── Models/
        │   ├── AppSettings.cs             serialised to disk
        │   └── AudioDeviceInfo.cs
        ├── Resources/Themes/
        │   ├── Light.xaml
        │   └── Dark.xaml
        └── Services/
            ├── SettingsService.cs
            ├── ThemeService.cs
            ├── AudioDeviceService.cs
            ├── HotKeyService.cs
            ├── AutoSwitchService.cs
            └── StartupService.cs
```

---

## Inspiration

This project is a from-scratch implementation informed by the excellent
open-source work of:

- **[Belphemur / SoundSwitch](https://github.com/Belphemur/SoundSwitch)** —
  the gold standard for Windows audio device hotkey switching on .NET.
- **[File-New-Project / EarTrumpet](https://github.com/File-New-Project/EarTrumpet)**
  — the reference design for a per-app / system-tray audio mixer with a
  modern look.
- **[xammen / BetterTrumpet](https://github.com/xammen/BetterTrumpet)** —
  fork of EarTrumpet showcasing themes, profiles, and CLI.

Audio Route is a smaller, focused tool: hotkeys + auto-switch on connect,
themed WPF UI, no per-app routing required.

---

## Known limitations

- **No per-application routing.** Audio Route only changes the system
  default (Multimedia + Communications). For Spotify-to-headset-while-game-to-
  speakers, you'll want EarTrumpet.
- **Acrylic / Mica backdrop not implemented.** The window paints a flat
  themed `BackgroundBrush`. The `UseAcrylicBackdrop` setting is reserved for
  a future addition (would require `SetWindowCompositionAttribute` on Win10
  Build 1903+ for Acrylic, or Windows 11 Build 22000+ for Mica).
- **Single instance enforcement is via tray, not a kernel object** — closing
  the launcher window does nothing because the app continues running in
  the tray. Use the tray menu "Exit" to fully quit.

---

## License

MIT — see [`LICENSE`](./LICENSE).
