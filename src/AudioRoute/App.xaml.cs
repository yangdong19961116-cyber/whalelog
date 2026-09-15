using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using AudioRoute.Services;
using Application = System.Windows.Application;

namespace AudioRoute;

public partial class App : Application
{
    public static SettingsService Settings { get; } = new();
    public static ThemeService Theme { get; } = new();
    public static AudioDeviceService Audio { get; } = new();
    public static HotKeyService HotKeys { get; } = new();
    public static AutoSwitchService AutoSwitch { get; } = new();
    public static NotifyIcon? TrayIcon { get; private set; }

    private MainWindow? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Bootstrap crash guard — never silently die on start.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ShowFatal(args.ExceptionException);

        DispatcherUnhandledException += (_, args) =>
        {
            ShowFatal(args.Exception);
            args.Handled = true;
        };

        Settings.Load();

        Theme.Start();
        Audio.Start();
        HotKeys.Start();
        AutoSwitch.Start();

        CreateTrayIcon();

        _main = new MainWindow();
        MainWindow = _main;

        bool startMinimized = Settings.Settings.StartWithWindows &&
                              e.Args.Length > 0 &&
                              e.Args[0].Equals("--autostart", StringComparison.OrdinalIgnoreCase);

        if (startMinimized)
        {
            _main.Hide();
        }
        else
        {
            _main.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { HotKeys.Stop(); } catch { }
        try { AutoSwitch.Stop(); } catch { }
        try { Audio.Stop(); } catch { }
        try { Theme.Stop(); } catch { }
        try { TrayIcon?.Dispose(); } catch { }
        base.OnExit(e);
    }

    private void CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open Audio Route");
        open.Click += (_, _) => ShowMainFromTray();
        var sep = new ToolStripSeparator();
        var cycle = new ToolStripMenuItem("Cycle next output");
        cycle.Click += (_, _) => Audio.CycleNext();
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => { TrayIcon.Visible = false; Shutdown(); };
        menu.Items.Add(open);
        menu.Items.Add(sep);
        menu.Items.Add(cycle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        TrayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Speaker,
            Visible = true,
            Text = "Audio Route",
            ContextMenuStrip = menu,
        };
        TrayIcon.DoubleClick += (_, _) => ShowMainFromTray();
        TrayIcon.BalloonTipTitle = "Audio Route";
        TrayIcon.BalloonTipText = "Running in background. Right-click for options.";
        TrayIcon.ShowBalloonTip(2000);
    }

    private void ShowMainFromTray()
    {
        if (_main == null) return;
        _main.Show();
        _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private static void ShowFatal(Exception? ex)
    {
        // Best-effort logging. We don't want the UI app to abort silently.
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AudioRoute", "logs");
            System.IO.Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            System.IO.File.WriteAllText(file, ex?.ToString() ?? "unknown error");
        }
        catch { /* swallow */ }
    }
}
