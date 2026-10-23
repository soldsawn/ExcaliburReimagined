using System.Windows;
using System.Windows.Controls;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Gpu;
using Excalibur.Core.Settings;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Ekran: the screen's refresh rate follows the charger (docs/08 3.1). The tray applies the rule; this page only
/// edits <see cref="UserSettings.RefreshRate"/>, which both processes share through the settings file.
/// Below it the graphics mode (A7, WMI 0x0203), which the service sets; Discrete is untried on this board and runs
/// with a safety net (<see cref="GpuTrial"/>), so it is asked for twice and its first start must be confirmed here.
/// </summary>
internal sealed class DisplayPage : UserControl
{
    private const string Highest = "En yüksek";

    private static readonly (GpuMode Mode, string Label, string Detail)[] GpuModes =
    [
        (GpuMode.Hybrid, "Karma", "önerilen · NVIDIA gerektiğinde çalışır"),
        (GpuMode.Integrated, "Yalnız dahili", "pil için · NVIDIA kapalı, HDMI çalışmaz"),
        (GpuMode.Discrete, "Yalnız NVIDIA", "denenmedi · yeniden başlatma gerekir"),
    ];

    private readonly UserSettingsStore _store = new();
    private readonly DisplayRefresh.Panel? _panel = DisplayRefresh.FindInternal();
    private readonly CheckBox _enabled = new() { Content = "Prizde yüksek, pilde düşük yenileme hızı kullan", FontSize = 14 };
    private readonly ComboBox _onAc, _onBattery;
    private readonly TextBlock _info;
    private bool _loading;

    private readonly FanConnection _connection;
    private readonly Dictionary<GpuMode, RadioButton> _gpuButtons = [];
    private readonly TextBlock _gpuNow, _gpuError;
    private readonly Button _restart;
    private readonly Border _trial;
    private bool _gpuRendering, _gpuBusy;

    public DisplayPage(FanConnection connection)
    {
        _connection = connection;
        _enabled.SetResourceReference(Control.ForegroundProperty, "Text");
        _onAc = Combo();
        _onBattery = Combo();
        _info = InfoPage.Text("", "Caption", top: 4, bottom: 10);

        var rates = _panel?.SupportedHz.OrderBy(h => h).ToList() ?? [];
        _onAc.ItemsSource = new[] { Highest }.Concat(rates.Select(Format)).ToList();
        _onBattery.ItemsSource = rates.Select(Format).ToList();

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(grid, 0, "Prize takılınca", _onAc);
        AddRow(grid, 1, "Pile geçince", _onBattery);

        _info.Text = _panel is null
            ? "Dizüstünün kendi ekranı bulunamadı; bu ayar şu an kullanılamıyor."
            : $"Ekran şu an {_panel.CurrentHz} Hz. Desteklenen hızlar: {string.Join(", ", rates)} Hz.";
        _enabled.IsEnabled = _onAc.IsEnabled = _onBattery.IsEnabled = _panel is not null;

        Load();
        _enabled.Checked += (_, _) => Save();
        _enabled.Unchecked += (_, _) => Save();
        _onAc.SelectionChanged += (_, _) => Save();
        _onBattery.SelectionChanged += (_, _) => Save();

        // ---- graphics mode ----
        var gpuButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (mode, label, detail) in GpuModes)
        {
            var b = new RadioButton
            {
                GroupName = "gpumode" + GetHashCode(), Margin = new Thickness(0, 0, 8, 8), MinWidth = 210, Height = 58,
                Content = new StackPanel
                {
                    Children = { new TextBlock { Text = label, FontWeight = FontWeights.SemiBold }, InfoPage.Text(detail, "Caption", top: 2) },
                },
            };
            b.SetResourceReference(StyleProperty, "Segment");
            b.Click += async (_, _) => await ChooseGpu(mode);
            _gpuButtons[mode] = b;
            gpuButtons.Children.Add(b);
        }
        _gpuNow = InfoPage.Text("", "Body", top: 4);
        _gpuError = InfoPage.Text("", "Caption", top: 6);
        _gpuError.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        _gpuError.TextWrapping = TextWrapping.Wrap;
        _restart = new Button { Content = "Şimdi yeniden başlat", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 170 };
        _restart.SetResourceReference(StyleProperty, "AccentButton");
        _restart.Click += (_, _) => Restart();
        _trial = TrialBanner();

        Content = new StackPanel
        {
            Children =
            {
                InfoPage.Text("Ekran", "PageTitle"),
                InfoPage.Text("Yenileme hızı prize ve pile göre kendiliğinden değişir.", "Caption", top: 2, bottom: 18),
                InfoPage.Card("Yenileme hızı",
                    _info,
                    _enabled, grid,
                    InfoPage.Text("Pilde düşük hız şarjı daha uzun tutar; prizde yüksek hız görüntüyü akıcı yapar. Kural, simge alanındaki uygulama açıkken çalışır.", "Caption", top: 6)),
                InfoPage.Card("Ekran kartı",
                    InfoPage.Text("Hangi ekran kartının kullanılacağını seçer. Karma çoğu zaman en iyisidir; yalnız dahili pili uzatır.", "Caption"),
                    gpuButtons, _gpuNow, _restart, _trial, _gpuError),
            },
        };

        _connection.Changed += RenderGpu;
        Loaded += (_, _) => RenderGpu();
    }

    private static string Format(int hz) => $"{hz} Hz";

    private static ComboBox Combo()
    {
        var c = new ComboBox { Margin = new Thickness(0, 0, 12, 8), HorizontalAlignment = HorizontalAlignment.Left };
        c.SetResourceReference(StyleProperty, "ThemedCombo");
        return c;
    }

    private static void AddRow(Grid grid, int row, string label, UIElement box)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = InfoPage.Text(label, "Body");
        l.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(l, row);
        Grid.SetRow(box, row);
        Grid.SetColumn(box, 1);
        grid.Children.Add(l);
        grid.Children.Add(box);
    }

    private void Load()
    {
        _loading = true;
        var rule = _store.Current.RefreshRate;
        _enabled.IsChecked = rule.Enabled;
        _onAc.SelectedItem = rule.OnAcHz == 0 ? Highest : Format(rule.OnAcHz);
        _onBattery.SelectedItem = Format(rule.OnBatteryHz);
        // A saved rate this screen does not offer shows as nothing selected; fall back to something valid.
        if (_onAc.SelectedItem is null) _onAc.SelectedIndex = 0;
        if (_onBattery.SelectedItem is null && _onBattery.Items.Count > 0) _onBattery.SelectedIndex = 0;
        _loading = false;
    }

    private void Save()
    {
        if (_loading || _panel is null) return;
        int ac = _onAc.SelectedItem as string is { } a && a != Highest ? int.Parse(a.Split(' ')[0]) : 0;
        int battery = _onBattery.SelectedItem as string is { } b ? int.Parse(b.Split(' ')[0]) : 60;
        var rule = new RefreshRateRule { Enabled = _enabled.IsChecked == true, OnAcHz = ac, OnBatteryHz = battery };
        if (!rule.IsValid) return;
        _store.Save(_store.Current with { RefreshRate = rule });
    }

    // ---- graphics mode ---------------------------------------------------------------------

    private Border TrialBanner()
    {
        var keep = new Button { Content = "Bu mod çalışıyor, kalsın", Margin = new Thickness(0, 10, 8, 0), MinWidth = 190 };
        keep.SetResourceReference(StyleProperty, "AccentButton");
        keep.Click += async (_, _) => await SendGpu(new FanRequest("confirmGpuMode"));
        var back = new Button { Content = "Karmaya dön", Margin = new Thickness(0, 10, 0, 0), MinWidth = 130 };
        back.SetResourceReference(StyleProperty, "PlainButton");
        back.Click += async (_, _) => await ChooseGpu(GpuMode.Hybrid);
        var text = InfoPage.Text("Bilgisayar yalnız NVIDIA modunda açıldı. Ekran düzgün çalışıyorsa \"kalsın\" deyin. " +
            "Demezseniz sonraki açılış güvenlik için karma modda olur.", "Body");
        text.TextWrapping = TextWrapping.Wrap;
        var banner = new Border
        {
            Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { text, new StackPanel { Orientation = Orientation.Horizontal, Children = { keep, back } } } },
        };
        banner.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        banner.SetResourceReference(Border.BorderBrushProperty, "Accent");
        return banner;
    }

    private static string Name(GpuMode mode) => GpuModes.First(m => m.Mode == mode).Label;

    private void RenderGpu()
    {
        var g = _connection.Status?.Gpu;
        _gpuRendering = true;
        foreach (var (mode, b) in _gpuButtons)
        {
            // The choice the next start brings is the one shown as picked.
            b.IsChecked = (g?.Pending ?? g?.Mode) == mode;
            b.IsEnabled = g?.Mode is not null && !_gpuBusy;
        }
        _gpuRendering = false;
        _gpuNow.Text = g?.Mode is not { } now ? "Ekran kartı modu okunamadı (fan servisi bağlı değil ya da bu bilgisayar desteklemiyor)."
            : g.Pending is { } next && next != now ? $"Şu an: {Name(now)}. Yeniden başlatınca: {Name(next)}."
            : g.RestartAdvised ? $"Şu an: {Name(now)}. NVIDIA kartı açıldı; güç ayarlarının tam geri gelmesi için bilgisayarı yeniden başlatın."
            : $"Şu an: {Name(now)}." + (g.Hdmi ? " Harici ekran bağlı." : "");
        _restart.Visibility = g?.RestartNeeded == true ? Visibility.Visible : Visibility.Collapsed;
        _trial.Visibility = g?.Trial == GpuTrial.AwaitingConfirm ? Visibility.Visible : Visibility.Collapsed;
        _gpuError.Visibility = _gpuError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ChooseGpu(GpuMode mode)
    {
        if (_gpuRendering || _gpuBusy) return;
        var g = _connection.Status?.Gpu;
        if ((g?.Pending ?? g?.Mode) == mode && g?.Trial != GpuTrial.AwaitingConfirm) { RenderGpu(); return; }
        if (mode == GpuMode.Discrete)
        {
            var answer = MessageBox.Show(
                "Yalnız NVIDIA modu bu bilgisayarda hiç denenmedi. Dahili ekran Intel ekran kartına bağlı olduğu için " +
                "yeniden başlatınca ekran siyah kalabilir.\n\n" +
                "Güvenlik için: bu modda ilk açılışta uygulama sonraki açılışı karma moda ayarlar. Ekran siyah kalırsa güç düğmesine " +
                "basılı tutup kapatın ve yeniden açın; bilgisayar karma modda açılır. Ekran düzgünse buradan \"kalsın\" dersiniz.\n\n" +
                "Yalnız NVIDIA modu seçilsin mi?",
                "Yalnız NVIDIA", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) { RenderGpu(); return; }
        }
        await SendGpu(new FanRequest("setGpuMode", GpuMode: mode));
    }

    private async Task SendGpu(FanRequest request)
    {
        _gpuBusy = true;
        RenderGpu();
        // Turning the NVIDIA card off or on takes seconds (pnputil, twice).
        var r = await _connection.RequestAsync(request, TimeSpan.FromSeconds(90));
        _gpuBusy = false;
        _gpuError.Text = r is null ? "Fan servisine ulaşılamadı." : r.Ok ? "" : r.Code switch
        {
            "gpu_hdmi" => "Harici ekran bağlıyken yalnız dahili mod seçilemez: HDMI çıkışı NVIDIA kartına bağlı.",
            "gpu_bitlocker" => "C: sürücüsü BitLocker ile korunuyor (ya da bu anlaşılamadı). Mod değişince kurtarma anahtarı istenebileceği için yalnız NVIDIA seçilemez.",
            "gpu_from_discrete" => "Yalnız NVIDIA modundan önce karmaya geçip yeniden başlatın.",
            "gpu_unavailable" => "Bu bilgisayarda ekran kartı modu okunamıyor.",
            _ => "Ekran kartı modu değiştirilemedi.",
        };
        RenderGpu();
        if (r is { Ok: true, RestartNeeded: true }) AskRestart();
    }

    private static void AskRestart()
    {
        if (MessageBox.Show("Yeni ekran kartı modu yeniden başlatınca geçerli olur. Şimdi yeniden başlatılsın mı?",
                "Yeniden başlat", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            Restart();
    }

    private static void Restart()
    {
        try { System.Diagnostics.Process.Start(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 0"); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"Yeniden başlatılamadı: {ex.Message}", "Yeniden başlat", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
