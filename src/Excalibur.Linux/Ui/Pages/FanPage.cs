using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Excalibur.App;
using Excalibur.App.Tray;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Linux.Ui.Controls;

namespace Excalibur.Linux.Ui.Pages;

/// <summary>Fan profiles, the curve chart, time-limited manual mode and the service's events, as on Windows (FanPage.xaml).</summary>
internal sealed class FanPage : UserControl
{
    private static readonly int[] Minutes = [1, 5, 10];
    private readonly FanConnection _connection;
    private int _minutes = Minutes[0];
    private IReadOnlyList<FanEventDto>? _shownEvents;
    // The faded trail: the last 10 minutes, refreshed every 10 s while the page is on screen.
    private readonly DispatcherTimer _trailTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _trailBusy;

    private readonly CurveChart _chart = new() { Height = 300 };
    private readonly TextBlock _curveTitle = Kit.Text("Eğri", "CardTitle"), _commandError, _writePath, _cpuSliderText, _gpuSliderText, _cpuLegendText, _stockLegendText;
    private readonly Control _cpuLegend, _gpuLegend, _stockLegend;
    private readonly Border _faultBanner;
    private readonly Slider _cpuSlider = Slider(), _gpuSlider = Slider();
    private readonly Button _applyManual = Kit.Button("Uygula", "AccentButton");
    private readonly StackPanel _eventList = new();

    internal CurveChart Chart => _chart;

    internal FanPage(FanConnection connection)
    {
        _connection = connection;
        var root = new StackPanel();
        root.Children.Add(Kit.Text("Soğutma", "PageTitle"));
        var subtitle = Kit.Text("Fanlar seçtiğiniz profile göre saniyede bir ayarlanır. Hangi profil seçili olursa olsun, ısınınca fanlar güvenlik için hızlanır.",
            "Caption", top: 2, bottom: 18);
        subtitle.FontSize = 13;
        root.Children.Add(subtitle);

        var clearFault = Kit.Button("Tekrar dene", "DangerButton");
        clearFault.Click += async (_, _) => ShowError(await _connection.SendAsync(new FanRequest("clearFault")));
        DockPanel.SetDock(clearFault, Dock.Right);
        var faultText = Kit.Text("Uygulama fan bilgilerini güvenle okuyamadı, bu yüzden fanları orijinal ayara bıraktı. \"Tekrar dene\" ile yeniden başlatabilirsiniz.", "Body")
            .Res(TextBlock.ForegroundProperty, "Bad");
        faultText.VerticalAlignment = VerticalAlignment.Center;
        _faultBanner = new Border { Margin = new Thickness(0, 0, 0, 14), IsVisible = false, Child = new DockPanel { Children = { clearFault, faultText } } }
            .Classed("CardBox").Res(Border.BorderBrushProperty, "Bad");
        root.Children.Add(_faultBanner);

        var selector = new ProfileSelector(connection);
        selector.CommandFailed += ShowError;
        _commandError = Kit.Text("", "Caption").Res(TextBlock.ForegroundProperty, "Bad");
        _commandError.IsVisible = false;
        root.Children.Add(new BracketCard
        {
            Child = new StackPanel { Children = { Kit.Text("Profil", "CardTitle", bottom: 12), selector, _commandError } },
        }.Classed("CardBox"));

        // the curve card: title on the left, the legend wrapping on the right
        _cpuLegendText = LegendText("Profil eğrisi");
        _stockLegendText = LegendText("Orijinal ayar");
        _cpuLegend = Legend(Swatch("Accent", 3), _cpuLegendText);
        _gpuLegend = Legend(Swatch("Ok", 3), LegendText("Ekran kartı eğrisi"));
        _stockLegend = Legend(Swatch("SubText", 2), _stockLegendText);
        var legend = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _cpuLegend, _gpuLegend, _stockLegend,
                Legend(Swatch("Bad", 2), LegendText("Alt sınır (koruma)")),
                Legend(Dot("Value"), LegendText("İşlemci şu an")),
                Legend(Dot("Ok"), LegendText("Ekran kartı şu an")),
                Legend(Swatch("SubText", 2, 0.5), LegendText("Son 10 dakikanın izi")),
            },
        };
        _writePath = Kit.Text("", "Caption", top: 4);
        _writePath.IsVisible = false;
        root.Children.Add(new BracketCard
        {
            Margin = new Thickness(0, 14, 0, 0),
            Child = new StackPanel
            {
                Children =
                {
                    new DockPanel { Margin = new Thickness(0, 0, 0, 10), Children = { _curveTitle, legend } },
                    _chart,
                    Kit.Text("İpucu: grafiğin üzerinde gezinin; renkli noktaların üzerine gelince fanın neden o hızda döndüğünü görürsünüz.", "Caption", top: 8),
                    _writePath,
                },
            },
        }.Classed("CardBox"));

        // manual speeds and the events side by side
        _cpuSliderText = SliderText();
        _gpuSliderText = SliderText();
        var sliders = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*,130"), RowDefinitions = new RowDefinitions("34,34") };
        AddRow(sliders, 0, "İşlemci", _cpuSlider, _cpuSliderText);
        AddRow(sliders, 1, "Ekran kartı", _gpuSlider, _gpuSliderText);
        var durations = new WrapPanel();
        foreach (int m in Minutes)
        {
            var b = Kit.Segment($"{m} dk", "duration" + GetHashCode());
            b.MinWidth = 64;
            b.IsChecked = m == _minutes;
            b.IsCheckedChanged += (_, _) => { if (b.IsChecked == true) _minutes = m; };
            durations.Children.Add(b);
        }
        _applyManual.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(_applyManual, Dock.Right);
        var manualCard = new BracketCard
        {
            Child = new StackPanel
            {
                Children =
                {
                    Kit.Text("Fanları elle ayarla", "CardTitle"),
                    Kit.Text("Seçtiğiniz hız, süre dolana kadar geçerli; sonra önceki profile döner. Isınırsa fanlar yine güvenlik için hızlanır.", "Caption", top: 4, bottom: 14),
                    sliders,
                    new DockPanel { Margin = new Thickness(0, 14, 0, 0), Children = { _applyManual, durations } },
                },
            },
        }.Classed("CardBox");
        var eventsTitle = Kit.Text("Son olaylar", "CardTitle");
        var eventsCaption = Kit.Text("Profil değişiklikleri, ısınma uyarıları ve sorunlar.", "Caption", top: 4, bottom: 10);
        DockPanel.SetDock(eventsTitle, Dock.Top);
        DockPanel.SetDock(eventsCaption, Dock.Top);
        var eventsCard = new BracketCard
        {
            Child = new DockPanel
            {
                Children =
                {
                    eventsTitle, eventsCaption,
                    new ScrollViewer { Height = 170, Content = _eventList, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
                },
            },
        }.Classed("CardBox");
        Grid.SetColumn(eventsCard, 2);
        root.Children.Add(new Grid { Margin = new Thickness(0, 14, 0, 0), ColumnDefinitions = new ColumnDefinitions("*,14,*"), Children = { manualCard, eventsCard } });
        Content = root;

        _cpuSlider.ValueChanged += (_, _) => SliderTexts();
        _gpuSlider.ValueChanged += (_, _) => SliderTexts();
        SliderTexts();
        _applyManual.Click += async (_, _) => ShowError(await _connection.SendAsync(
            new FanRequest("setManual", Cpu: (int)_cpuSlider.Value, Gpu: (int)_gpuSlider.Value, Minutes: _minutes)));

        _connection.Changed += Render;
        _trailTimer.Tick += async (_, _) => await RefreshTrail();
        AttachedToVisualTree += async (_, _) => { _trailTimer.Start(); await RefreshTrail(); };
        DetachedFromVisualTree += (_, _) => _trailTimer.Stop();
        Render();
    }

    private static Slider Slider() => new Slider { Minimum = 25, Maximum = 100, Value = 60, VerticalAlignment = VerticalAlignment.Center }.Classed("ThinSlider");

    private static TextBlock SliderText()
    {
        var t = Kit.Text("", "Mono").Res(TextBlock.ForegroundProperty, "Text");
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(14, 0, 0, 0);
        return t;
    }

    private static void AddRow(Grid grid, int row, string label, Slider slider, TextBlock value)
    {
        var text = Kit.Text(label, "Body");
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(text, row);
        Grid.SetRow(slider, row);
        Grid.SetColumn(slider, 1);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 2);
        grid.Children.Add(text);
        grid.Children.Add(slider);
        grid.Children.Add(value);
    }

    // Each swatch and its words are one piece, so a wrap never parts them.
    private static Control Legend(Control swatch, TextBlock text) =>
        new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, text } };

    private static TextBlock LegendText(string text)
    {
        var t = Kit.Text(text, "Caption");
        t.Margin = new Thickness(6, 0, 16, 0);
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    private static Rectangle Swatch(string brush, double height, double opacity = 1) =>
        new Rectangle { Width = 16, Height = height, Opacity = opacity, VerticalAlignment = VerticalAlignment.Center }.Res(Shape.FillProperty, brush);

    private static Ellipse Dot(string brush) =>
        new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center }.Res(Shape.FillProperty, brush);

    private async Task RefreshTrail()
    {
        if (_trailBusy) return;
        _trailBusy = true;
        try { _chart.ShowTrail(await _connection.HistoryAsync(TimeSpan.FromMinutes(10), 300)); }
        finally { _trailBusy = false; }
    }

    private void SliderTexts()
    {
        // The EC runs the fans at about 54 rpm per percent (docs/06).
        _cpuSliderText.Text = $"{"%" + _cpuSlider.Value.ToString("0"),4} ~{_cpuSlider.Value * 54,4:0} rpm";
        _gpuSliderText.Text = $"{"%" + _gpuSlider.Value.ToString("0"),4} ~{_gpuSlider.Value * 54,4:0} rpm";
    }

    private void ShowError(string? error)
    {
        _commandError.Text = error ?? "";
        _commandError.IsVisible = error is not null;
    }

    private void Render()
    {
        var s = _connection.Status;
        bool on = _connection.Connected && s is not null;
        _applyManual.IsEnabled = on;
        _faultBanner.IsVisible = on && s!.Mode == FanLoopMode.Faulted;
        _writePath.IsVisible = on && s!.Mode == FanLoopMode.Running;
        if (on) _writePath.Text = WritePathLine;
        if (!on)
        {
            _chart.Show(null, null);
            _curveTitle.Text = "Eğri";
            return;
        }

        var profile = s!.Profile == "Manual" && s.CpuTarget is { } mc && s.GpuTarget is { } mg
            ? FanProfile.Manual(mc, mg)
            : s.Profile == "EcAuto" ? FanProfile.EcAuto : _connection.Catalog.Find(s.Profile);
        bool separateGpu = profile is { IsEcAuto: false } p && !p.CpuCurve.Points.SequenceEqual(p.GpuCurve.Points);
        _gpuLegend.IsVisible = separateGpu;
        // Original settings draw no profile curve: the legend's gray line already says which one is theirs.
        _cpuLegend.IsVisible = profile?.IsEcAuto != true;
        _cpuLegendText.Text = separateGpu ? "İşlemci eğrisi" : "Profil eğrisi";
        _stockLegend.IsVisible = _chart.ShowsStock;
        _stockLegendText.Text = _chart.StockLabel;
        _curveTitle.Text = $"Eğri — {ProfileNames.Label(s.Profile, s.ManualUntil)}";
        _chart.Show(profile, s);

        if (!ReferenceEquals(_shownEvents, _connection.Events))
        {
            _shownEvents = _connection.Events;
            _eventList.Children.Clear();
            foreach (var e in _connection.Events.Reverse())
            {
                var time = new TextBlock { Text = e.At.ToString("HH:mm:ss"), FontSize = 12, Width = 68 }
                    .Res(TextBlock.FontFamilyProperty, "MonoFont").Res(TextBlock.ForegroundProperty, "SubText");
                var message = new TextBlock { Text = Texts.Event(e.Message), FontSize = 12.5, TextWrapping = TextWrapping.Wrap }
                    .Res(TextBlock.ForegroundProperty, "Text");
                _eventList.Children.Add(new DockPanel { Margin = new Thickness(0, 0, 0, 5), Children = { time, message } });
            }
        }
    }

    /// <summary>How the fans are written on Linux: always straight to the fan controller (the BIOS path needs Windows' WMI).</summary>
    public const string WritePathLine = "Fan kontrolü: doğrudan fan denetleyicisine yazılıyor.";
}
