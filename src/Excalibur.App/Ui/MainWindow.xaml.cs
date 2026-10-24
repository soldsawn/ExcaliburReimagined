using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Excalibur.App.Ui.Pages;
using Excalibur.Core.Fan;

namespace Excalibur.App.Ui;

/// <summary>
/// The app: a sidebar of pages (the Control Center's features as they get built, docs/08) and a
/// status bar for the fan service. Pages are created when first opened and kept while the
/// window lives; closing the window ends this process (the tray keeps running).
/// </summary>
public partial class MainWindow : Window
{
    private sealed record PageDef(string Key, string Title, string Icon, Func<UserControl> Create, bool Bottom = false);

    private readonly FanConnection _connection;
    private readonly Dictionary<string, UserControl> _pages = [];
    private readonly List<PageDef> _defs;

    internal MainWindow(FanConnection connection)
    {
        _connection = connection;
        InitializeComponent();

        // Icons are Segoe Fluent Icons code points. Pages not built yet describe what is coming (docs/08).
        _defs =
        [
            new("overview", "Panel", "\uE80F", () => new OverviewPage(_connection)),
            new("fan", "Soğutma", "\uE9CA", () => new FanPage(_connection)),
            new("history", "Geçmiş", "", () => new HistoryPage(_connection)),
            new("profiles", "Profiller", "", () => new ProfilesPage(_connection)),
            new("power", "Performans", "", () => new PowerPage(_connection)),
            new("battery", "Pil", "", () => new BatteryPage(_connection, () => Open("settings"))),
            new("display", "Ekran", "", () => new DisplayPage()),
            new("lighting", "Aydınlatma", "\uE765", () => new LightingPage(_connection)),
            new("automation", "Otomasyon", "", () => new AutomationPage(_connection)),
            new("settings", "Ayarlar", "\uE713", () => InfoPage.Settings(_connection), Bottom: true),
        ];
        foreach (var def in _defs)
        {
            var button = new RadioButton { Content = def.Title, Tag = def.Icon, Style = (Style)FindResource("NavButton") };
            button.Checked += (_, _) => ShowPage(def);
            _buttons[def.Key] = button;
            (def.Bottom ? NavBottom : Nav).Children.Add(button);
        }
        ((RadioButton)Nav.Children[0]).IsChecked = true;

        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close();
        StateChanged += (_, _) =>
        {
            MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            // A maximized chrome window overhangs the screen by the resize border.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            // The overhang itself is clipped, but Windows 11 still draws its 1 px frame on the real window
            // edge, which then shows as a thin line on the neighbouring monitor. No frame while maximized.
            int border = WindowState == WindowState.Maximized ? DwmColorNone : DwmColorDefault;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmaBorderColor, ref border, sizeof(int));
        };

        _connection.Changed += RenderStatus;
        Loaded += (_, _) => _connection.Start();
        RenderStatus();
    }

    private readonly Dictionary<string, RadioButton> _buttons = [];

    private void Open(string key) => _buttons[key].IsChecked = true;

    private void ShowPage(PageDef def)
    {
        if (!_pages.TryGetValue(def.Key, out var page)) _pages[def.Key] = page = def.Create();
        PageHost.Content = page;
        PageScroll.ScrollToTop();
    }

    private void RenderStatus()
    {
        var s = _connection.Status;
        if (!_connection.Connected || s is null)
        {
            StatusDot.Fill = (Brush)FindResource("Off");
            StatusText.Text = "Fan kontrolüne bağlanılamıyor. Sorun sürerse bilgisayarı yeniden başlatın.";
            UpdatedText.Text = "";
            return;
        }
        (string state, string brush) = s.Mode switch
        {
            FanLoopMode.Running => ("Fan kontrolü açık", "Ok"),
            FanLoopMode.MonitorOnly when s.EcSeenElsewhere => ("Fanlar yalnız izleniyor: fan kontrolü için kısa bir ayar gerekiyor", "Warn"),
            FanLoopMode.MonitorOnly => ("Fanlar yalnız izleniyor: bu dizüstünün fan yazılımı tanınmadı", "Warn"),
            FanLoopMode.Faulted => ("Sorun var: fanlar orijinal ayara bırakıldı", "Bad"),
            _ => ("Fan kontrolü durdu", "Off"),
        };
        StatusDot.Fill = (Brush)FindResource(brush);
        StatusText.Text = string.Join("   ·   ", new[]
        {
            state + (s.Simulated ? " (deneme modu)" : ""),
            Texts.Driver(s.HoldsFans),
            $"açık kalma süresi: {Texts.Duration(DateTimeOffset.Now - s.ServiceStartedAt)}",
        });
        UpdatedText.Text = _connection.LastUpdate is { } at ? $"güncellendi {at:HH:mm:ss}" : "";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Windows 11: rounded corners and a dark frame to match the theme; ignored elsewhere.
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2, dark = (bool)FindResource("IsDark") ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    /// <summary>Development: after the first data arrives, save each built page as a PNG and exit.</summary>
    internal void SnapshotAndClose(string path)
    {
        // Long enough for the first status and the history trail to arrive.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            foreach (var (key, index) in new[] { ("overview", 0), ("fan", 1), ("history", 2), ("profiles", 3), ("power", 4), ("battery", 5), ("display", 6), ("lighting", 7), ("automation", 8), ("settings", -1) })
            {
                var button = index >= 0 ? (RadioButton)Nav.Children[index] : (RadioButton)NavBottom.Children[0];
                button.IsChecked = true;
                await Task.Delay(1200); // pages that fetch on show (the fan page's trail) get their data
                Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                UpdateLayout();
                Save(Root, Path.ChangeExtension(path, $".{key}.png"));
                if (_pages.GetValueOrDefault(key) is FanPage fan)
                {
                    fan.Chart.HoverAt(62, 50);
                    UpdateLayout();
                    Save(Root, Path.ChangeExtension(path, ".fan-crosshair.png"));
                    fan.Chart.HoverCpuDot();
                    UpdateLayout();
                    Save(Root, Path.ChangeExtension(path, ".fan-dot.png"));
                }
                if (_pages.GetValueOrDefault(key) is HistoryPage history)
                {
                    history.Chart.HoverAt(0.8);
                    UpdateLayout();
                    Save(Root, Path.ChangeExtension(path, ".history-hover.png"));
                }
            }
            Close();
        };
        Loaded += (_, _) => timer.Start();
    }

    private static void Save(FrameworkElement element, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var bmp = new RenderTargetBitmap((int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(element);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var file = File.Create(path);
        png.Save(file);
    }

    private const int DwmaBorderColor = 34, DwmColorNone = unchecked((int)0xFFFFFFFE), DwmColorDefault = unchecked((int)0xFFFFFFFF);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
