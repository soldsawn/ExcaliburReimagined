using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Excalibur.App;
using Excalibur.Core.Fan;
using Excalibur.Linux.Ui.Pages;

namespace Excalibur.Linux.Ui;

/// <summary>
/// The window, as on Windows (Excalibur.App/Ui/MainWindow): a sidebar of pages and a status bar for the fan service. Pages are
/// created when first opened and kept while the window lives. The desktop draws the window frame (Linux window managers
/// decorate windows themselves).
/// </summary>
internal sealed class MainWindow : Window
{
    private sealed record PageDef(string Key, string Title, string Icon, Func<Control> Create, bool Bottom = false);

    private readonly FanConnection _connection;
    private readonly Dictionary<string, Control> _pages = [];
    private readonly Dictionary<string, RadioButton> _buttons = [];
    private readonly List<PageDef> _defs;
    private readonly ScrollViewer _scroll;
    private readonly ContentControl _host = new() { Margin = new Thickness(32, 26, 32, 28), Focusable = false };
    private readonly Ellipse _statusDot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusText = Kit.Text("", "Caption"), _updatedText = Kit.Text("", "Caption");
    private readonly Grid _root;

    public bool HideOnClose { get; set; }
    private bool _exiting;

    internal MainWindow(FanConnection connection, Stream? icon)
    {
        _connection = connection;
        Title = "Excalibur Reimagined";
        Width = 1180;
        Height = 780;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (icon is not null) Icon = new WindowIcon(icon);

        // Icons are Fluent System Icons code points (the Windows window's Segoe Fluent Icons).
        _defs =
        [
            new("overview", "Panel", "", () => new OverviewPage(_connection)),
            new("fan", "Soğutma", "", () => new FanPage(_connection)),
            new("history", "Geçmiş", "", () => new HistoryPage(_connection)),
            new("profiles", "Profiller", "", () => new ProfilesPage(_connection)),
            new("power", "Performans", "", () => new PowerPage(_connection)),
            new("battery", "Pil", "", () => new BatteryPage(_connection)),
            new("lighting", "Aydınlatma", "", () => Soon("Aydınlatma")),
            new("automation", "Otomasyon", char.ConvertFromUtf32(0xF013D), () => Soon("Otomasyon")),
            new("settings", "Ayarlar", "", () => Soon("Ayarlar"), Bottom: true),
        ];

        // sidebar: brand, navigation, settings at the bottom
        var brand = new StackPanel
        {
            Margin = new Thickness(22, 18, 0, 22),
            Children =
            {
                new TextBlock { Text = "Excalibur", FontWeight = FontWeight.SemiBold, FontSize = 28 }.Res(TextBlock.ForegroundProperty, "Accent"),
                new TextBlock { Text = "R E I M A G I N E D", FontSize = 10, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 0, 0, 0) }
                    .Res(TextBlock.ForegroundProperty, "SubText"),
                new Border { Height = 2, Width = 150, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), CornerRadius = new CornerRadius(1) }
                    .Res(Border.BackgroundProperty, "BrandLine"),
            },
        };
        var nav = new StackPanel();
        var navBottom = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var def in _defs)
        {
            var button = new RadioButton { Content = def.Title, Tag = def.Icon, GroupName = "Nav" }.Classed("NavButton");
            button.IsCheckedChanged += (_, _) => { if (button.IsChecked == true) ShowPage(def); };
            _buttons[def.Key] = button;
            (def.Bottom ? navBottom : nav).Children.Add(button);
        }
        var sideDock = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(brand, Dock.Top);
        DockPanel.SetDock(nav, Dock.Top);
        DockPanel.SetDock(navBottom, Dock.Bottom);
        sideDock.Children.Add(brand);
        sideDock.Children.Add(nav);
        sideDock.Children.Add(navBottom);
        var sidebar = new Border { Child = sideDock }.Res(Border.BackgroundProperty, "Sidebar");

        _scroll = new ScrollViewer { Content = _host, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetColumn(_scroll, 1);

        // status bar
        _statusText.TextWrapping = TextWrapping.NoWrap;
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _updatedText.TextWrapping = TextWrapping.NoWrap;
        _updatedText.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_updatedText, Dock.Right);
        var statusDock = new DockPanel { Margin = new Thickness(16, 0), VerticalAlignment = VerticalAlignment.Center };
        statusDock.Children.Add(_updatedText);
        statusDock.Children.Add(_statusDot);
        statusDock.Children.Add(_statusText);
        var statusBar = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = statusDock }
            .Res(Border.BackgroundProperty, "Sidebar").Res(Border.BorderBrushProperty, "Border");
        Grid.SetRow(statusBar, 1);
        Grid.SetColumnSpan(statusBar, 2);

        _root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("232,*"),
            RowDefinitions = new RowDefinitions("*,32"),
            Children = { sidebar, _scroll, statusBar },
        }.Res(Panel.BackgroundProperty, "Bg");
        Content = _root;

        _buttons["overview"].IsChecked = true;
        _connection.Changed += RenderStatus;
        Opened += (_, _) => _connection.Start();
        Closing += (_, e) =>
        {
            // With a tray icon the window only hides; the fans never depend on it either way.
            if (HideOnClose && !_exiting) { e.Cancel = true; Hide(); }
        };
        RenderStatus();
    }

    public void Exit()
    {
        _exiting = true;
        Close();
    }

    private static Control Soon(string title) =>
        Kit.Page(title, "Bu sayfa Linux'a taşınıyor; şimdilik komut satırından (exr) kullanılabilir.");

    private void ShowPage(PageDef def)
    {
        if (!_pages.TryGetValue(def.Key, out var page)) _pages[def.Key] = page = def.Create();
        _host.Content = page;
        _scroll.Offset = default;
    }

    private void RenderStatus()
    {
        var s = _connection.Status;
        if (!_connection.Connected || s is null)
        {
            _statusDot.Fill = Palette.Brush(this, _connection.Problem is null ? "Off" : "Bad");
            _statusText.Text = _connection.Problem is { } problem
                ? $"Fan kontrolü kapalı: {problem}"
                : "Fan kontrolüne bağlanılamıyor. Hizmet çalışıyor mu? sudo systemctl status excalibur-reimagined";
            _updatedText.Text = "";
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
        _statusDot.Fill = Palette.Brush(this, brush);
        _statusText.Text = string.Join("   ·   ", new[]
        {
            state + (s.Simulated ? " (deneme modu)" : ""),
            Texts.Driver(s.HoldsFans),
            $"açık kalma süresi: {Texts.Duration(DateTimeOffset.Now - s.ServiceStartedAt)}",
        });
        _updatedText.Text = _connection.LastUpdate is { } at ? $"güncellendi {at:HH:mm:ss}" : "";
    }

    /// <summary>Development: after the first data arrives, save each page as a PNG (headless or on screen) and close.</summary>
    internal async Task SnapshotAndCloseAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await Task.Delay(4000); // the first status, profiles and settings
        foreach (var def in _defs)
        {
            _buttons[def.Key].IsChecked = true;
            await Task.Delay(1200);
            Dispatcher.UIThread.RunJobs();
            var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size);
            bitmap.Render(_root);
            bitmap.Save(System.IO.Path.Combine(directory, $"{def.Key}.png"));
        }
        Exit();
    }
}
