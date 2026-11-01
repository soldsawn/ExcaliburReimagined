using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.Markup.Xaml.Styling;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.Linux.Ui;

/// <summary>
/// The Linux window application: <see cref="MainWindow"/> (the Windows window's pages in Avalonia), the tray icon where the
/// desktop shows one, one window per user, and headless PNG snapshots for development (<c>gui --snapshot &lt;dir&gt;</c>).
/// </summary>
internal sealed class LinuxGuiApp : Application
{
    private static LinuxPaths? _paths;
    private static string? _snapshotDir;
    private static bool _headless;

    private MainWindow? _window;
    private FanConnection? _connection;
    private UserSettingsStore? _userSettings;

    public static bool HasDisplaySession() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    public static Stream? OpenEmbeddedIcon() =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream("app-256.png");

    public static int ExtractIcon(string destinationPath)
    {
        using var stream = OpenEmbeddedIcon();
        if (stream is null) return 1;
        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(destinationPath);
        stream.CopyTo(fs);
        return 0;
    }

    public static int Run(LinuxPaths paths, string? snapshotDir = null)
    {
        // One window per user: a second launch (menu, terminal) brings the running one forward instead.
        if (snapshotDir is null && TryShowRunningInstance()) return 0;
        _paths = paths;
        _snapshotDir = snapshotDir;
        _headless = snapshotDir is not null && !HasDisplaySession();

        var builder = AppBuilder.Configure<LinuxGuiApp>();
        if (_headless)
            builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        else
            builder.UsePlatformDetect().UseSkia();
        return builder.StartWithClassicDesktopLifetime([]);
    }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://excalibur-reimagined/")) { Source = new Uri("avares://excalibur-reimagined/Ui/Theme.axaml") });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && _paths is not null)
        {
            // The appearance (theme, accent, number font, corner marks) is the Windows window's settings file, under ~/.config.
            _userSettings = new UserSettingsStore();
            Palette.Apply(Resources, _userSettings.Current);
            _userSettings.Changed += s => Dispatcher.UIThread.Post(() => Palette.Apply(Resources, s));

            _connection = new FanConnection(_paths);
            using var iconStream = OpenEmbeddedIcon();
            // Hide to the tray only where the desktop shows tray icons (GNOME without its AppIndicator extension does not);
            // otherwise closing the window would leave it running with no way back.
            bool hasTray = !_headless && TrayHostAvailable() && SetupTrayIcon(desktop);
            if (_snapshotDir is null) ListenForOtherInstances();
            _window = new MainWindow(_connection, iconStream) { HideOnClose = hasTray && _snapshotDir is null };
            desktop.MainWindow = _window;

            if (_snapshotDir is { } dir)
            {
                _window.Opened += async (_, _) =>
                {
                    await _window.SnapshotAndCloseAsync(dir);
                    desktop.Shutdown(0);
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private bool SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_connection is not { } connection) return false;
        try
        {
            using var iconStream = OpenEmbeddedIcon();
            if (iconStream is null) return false;

            var openItem = new NativeMenuItem("Pencereyi aç");
            openItem.Click += (_, _) => ShowMainWindow();

            var profileMenu = new NativeMenu();
            foreach (var (key, label, _) in Excalibur.App.Tray.ProfileNames.All)
            {
                var item = new NativeMenuItem(label);
                item.Click += async (_, _) => await connection.SendAsync(new FanRequest("setProfile", Profile: key));
                profileMenu.Items.Add(item);
            }

            var powerMenu = new NativeMenu();
            foreach (var (mode, label) in new[] { (PowerMode.Office, "Ofis"), (PowerMode.Gaming, "Oyun"), (PowerMode.HighPerformance, "Yüksek performans") })
            {
                var item = new NativeMenuItem(label);
                item.Click += async (_, _) => await connection.SendAsync(new FanRequest("setPowerMode", PowerMode: mode));
                powerMenu.Items.Add(item);
            }

            var exitItem = new NativeMenuItem("Çıkış (fan hizmeti çalışmaya devam eder)");
            exitItem.Click += (_, _) =>
            {
                _window?.Exit();
                desktop.Shutdown(0);
            };

            var menu = new NativeMenu
            {
                Items =
                {
                    openItem,
                    new NativeMenuItem("Fan profili") { Menu = profileMenu },
                    new NativeMenuItem("Güç modu") { Menu = powerMenu },
                    new NativeMenuItemSeparator(),
                    exitItem,
                },
            };

            var tray = new TrayIcon
            {
                Icon = new WindowIcon(iconStream),
                ToolTipText = "Excalibur Reimagined",
                Menu = menu,
                IsVisible = true,
            };
            tray.Clicked += (_, _) => ShowMainWindow();
            TrayIcon.SetIcons(this, [tray]);

            // Nothing in the menu changes while it may be open: GNOME's AppIndicator rebuilds an open menu on every change, which
            // closed the submenus a second after they opened (live temperatures were in the first row, 2026-11-01). The tooltip
            // names the profile and changes only with it.
            string? shownProfile = null;
            connection.Changed += () =>
            {
                if (connection.Status is not { } s || s.Profile == shownProfile) return;
                shownProfile = s.Profile;
                tray.ToolTipText = $"Excalibur Reimagined — {Excalibur.App.Tray.ProfileNames.Label(s.Profile)}";
            };
            // The window polls only while it exists; the tray's line needs it running even while the window is hidden.
            connection.Start();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static string InstanceSocketPath() =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
            ? Path.Combine(runtime, "excalibur-reimagined-gui.sock")
            : Path.Combine(Path.GetTempPath(), $"excalibur-reimagined-gui-{Environment.UserName}.sock");

    /// <summary>True when another window of this user answered and was asked to show itself.</summary>
    private static bool TryShowRunningInstance()
    {
        string path = InstanceSocketPath();
        if (!File.Exists(path)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            socket.Send("show\n"u8);
            return true;
        }
        catch (SocketException)
        {
            // Left over from a window that did not close cleanly.
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    private void ListenForOtherInstances()
    {
        string path = InstanceSocketPath();
        Socket listener;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            return; // no single-instance guard; the window still works
        }
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var client = listener.Accept();
                    Dispatcher.UIThread.Post(ShowMainWindow);
                }
                catch (SocketException) { return; }
            }
        }) { IsBackground = true, Name = "gui-instance" };
        thread.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { listener.Dispose(); File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException) { }
        };
    }

    /// <summary>Whether a tray host (StatusNotifierWatcher, which Avalonia's tray icon needs) runs on the session bus.</summary>
    private static bool TrayHostAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("dbus-send",
                "--session --print-reply --reply-timeout=2000 --dest=org.freedesktop.DBus /org/freedesktop/DBus " +
                "org.freedesktop.DBus.NameHasOwner string:org.kde.StatusNotifierWatcher")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000)) return false;
            return output.Contains("boolean true", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}
