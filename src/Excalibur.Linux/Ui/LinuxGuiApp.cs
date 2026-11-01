using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Excalibur.Core.Power;

namespace Excalibur.Linux.Ui;

/// <summary>
/// Avalonia desktop application for Linux (L3): hosts <see cref="LinuxMainWindow"/> and the system tray icon
/// (<see cref="TrayIcon"/>), and supports headless Skia PNG snapshot rendering (<c>--snapshot &lt;dir&gt;</c>).
/// </summary>
internal sealed class LinuxGuiApp : Application
{
    private static LinuxGuiClient? _client;
    private static string? _snapshotDir;
    private static bool _headless;

    private LinuxMainWindow? _window;
    private NativeMenuItem? _tempItem;

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

    public static int Run(LinuxPaths paths, string version, string? snapshotDir = null)
    {
        // One window per user: a second launch (menu, terminal) brings the running one forward instead.
        if (snapshotDir is null && TryShowRunningInstance()) return 0;
        _client = new LinuxGuiClient(paths, version);
        _snapshotDir = snapshotDir;
        _headless = snapshotDir is not null && !HasDisplaySession();

        var builder = AppBuilder.Configure<LinuxGuiApp>();
        if (_headless)
        {
            builder.UseSkia()
                   .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        }
        else
        {
            builder.UsePlatformDetect()
                   .UseSkia();
        }
        return builder.StartWithClassicDesktopLifetime([]);
    }

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && _client is not null)
        {
            using var iconStream = OpenEmbeddedIcon();
            // Hide to the tray only where the desktop shows tray icons (GNOME without its AppIndicator extension does not);
            // otherwise closing the window would leave it running with no way back.
            bool hasTray = !_headless && TrayHostAvailable() && SetupTrayIcon(desktop);
            if (_snapshotDir is null) ListenForOtherInstances();
            _window = new LinuxMainWindow(_client, hideToTrayOnClose: hasTray && _snapshotDir is null, iconStream);
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
        if (_client is null) return false;
        try
        {
            using var iconStream = OpenEmbeddedIcon();
            if (iconStream is null) return false;

            _tempItem = new NativeMenuItem("Excalibur Reimagined — Bağlanıyor...") { IsEnabled = false };
            var openItem = new NativeMenuItem("Pencereyi Aç");
            openItem.Click += (_, _) => ShowMainWindow();

            var profileMenu = new NativeMenu();
            foreach (var (key, label) in new[]
            {
                ("Quiet", "Sessiz"),
                ("Balanced", "Dengeli"),
                ("Performance", "Performans"),
                ("EcAuto", "Orijinal (EC)"),
            })
            {
                string profKey = key;
                var item = new NativeMenuItem(label);
                item.Click += (_, _) => _client.Send(new LinuxRequest("setProfile", Profile: profKey));
                profileMenu.Items.Add(item);
            }

            var powerMenu = new NativeMenu();
            foreach (var (mode, label) in new[]
            {
                (PowerMode.Office, "Ofis"),
                (PowerMode.Gaming, "Oyun"),
                (PowerMode.HighPerformance, "Yüksek performans"),
            })
            {
                PowerMode target = mode;
                var item = new NativeMenuItem(label);
                item.Click += (_, _) => _client.Send(new LinuxRequest("setPowerMode", PowerMode: target));
                powerMenu.Items.Add(item);
            }

            var exitItem = new NativeMenuItem("Çıkış (Servis Arkada Çalışmaya Devam Eder)");
            exitItem.Click += (_, _) =>
            {
                _window?.ExitApplication();
                desktop.Shutdown(0);
            };

            var menu = new NativeMenu
            {
                Items =
                {
                    _tempItem,
                    new NativeMenuItemSeparator(),
                    openItem,
                    new NativeMenuItem("Fan Profili") { Menu = profileMenu },
                    new NativeMenuItem("Güç Modu") { Menu = powerMenu },
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

            _client.Changed += () => Dispatcher.UIThread.Post(() =>
            {
                if (_client.Status is { } s)
                {
                    string summary = $"CPU {s.CpuTempC?.ToString() ?? "-"} °C · GPU {s.GpuTempC?.ToString() ?? "-"} °C · {LinuxMainWindow.ProfileLabel(s.Profile)}";
                    if (_tempItem is not null) _tempItem.Header = summary;
                    tray.ToolTipText = $"Excalibur Reimagined — {summary}";
                }
            });
            return true;
        }
        catch
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
