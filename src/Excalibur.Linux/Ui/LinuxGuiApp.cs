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
            bool hasTray = !_headless && SetupTrayIcon(desktop);
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

    private void ShowMainWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}
