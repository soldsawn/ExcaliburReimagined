using System.Windows;
using System.Windows.Controls;
using Excalibur.App.Ui.Controls;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Graphics;
using Excalibur.Core.Settings;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Ekran: the screen's refresh rate follows the charger (docs/08 3.1). The tray applies the rule; this page only
/// edits <see cref="UserSettings.RefreshRate"/>, which both processes share through the settings file. Below it the
/// graphics mode the BIOS reports (the service reads it; .scratch/ekran-karti-modu).
/// </summary>
internal sealed class DisplayPage : UserControl
{
    private const string Highest = "En yüksek";

    private readonly UserSettingsStore _store = new();
    private readonly DisplayRefresh.Panel? _panel = DisplayRefresh.FindInternal();
    private readonly CheckBox _enabled = new() { Content = "Prizde yüksek, pilde düşük yenileme hızı kullan", FontSize = 14 };
    private readonly ComboBox _onAc, _onBattery;
    private readonly TextBlock _info;
    private readonly TextBlock _gpuNow = InfoPage.Text("", "Body", bottom: 8);
    private readonly TextBlock _gpuError = InfoPage.Text("", "Caption", top: 4);
    private readonly TextBlock _gpuRestart = InfoPage.Text(
        "Ekran kartının güç ayarlarının tam yerine gelmesi için bilgisayarı yeniden başlatmanız önerilir.", "Caption", top: 4);
    private readonly Dictionary<GraphicsMode, RadioButton> _gpuButtons = [];
    private readonly FanConnection _connection;
    private bool _loading, _rendering;

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
                InfoPage.Card("Ekran kartı modu",
                    _gpuNow,
                    GraphicsButtons(),
                    _gpuError, _gpuRestart,
                    InfoPage.Text("• Hibrit: iki ekran kartı birlikte; NVIDIA kartı yalnız gerektiğinde çalışır. Önerilen.", "Caption", top: 6),
                    InfoPage.Text("• Dahili: yalnız Intel; NVIDIA kartı kapanır, pil daha uzun gider. HDMI çıkışı çalışmaz; HDMI kablosu takılınca kendiliğinden Hibrit'e dönülür.", "Caption", top: 2),
                    InfoPage.Text("• Harici: yalnız NVIDIA; oyunlarda en yüksek hız, pil daha kısa. Yeniden başlatma gerekir (yakında).", "Caption", top: 2)),
            },
        };

        ShowError(null);
        _connection.Changed += RenderGraphics;
        RenderGraphics();
    }

    private WrapPanel GraphicsButtons()
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        string group = "graphics" + GetHashCode();
        foreach (var (mode, label, detail) in new[]
        {
            (GraphicsMode.Hybrid, "Hibrit", "iki ekran kartı · önerilen"),
            (GraphicsMode.Integrated, "Dahili", "yalnız Intel · pil uzun"),
            (GraphicsMode.Discrete, "Harici", "yalnız NVIDIA · yakında"),
        })
        {
            var b = new RadioButton
            {
                GroupName = group, Margin = new Thickness(0, 0, 8, 8), MinWidth = 180, Height = 58,
                Content = new StackPanel { Children = { new TextBlock { Text = label, FontWeight = FontWeights.SemiBold }, InfoPage.Text(detail, "Caption", top: 2) } },
            };
            b.SetResourceReference(StyleProperty, "Segment");
            b.Checked += async (_, _) =>
            {
                if (_rendering) return;
                if (mode == GraphicsMode.Integrated && !ConfirmDialog.Ask(this, "Dahili moda geç",
                        "NVIDIA ekran kartı kapanacak; açık oyun ya da ekran kartını kullanan programlar kapanabilir. Kaydedilmemiş işleriniz varsa önce kaydedin.",
                        "Dahili moda geç"))
                {
                    RenderGraphics();
                    return;
                }
                var r = await _connection.RequestAsync(new FanRequest("setGraphicsMode", GraphicsMode: mode));
                ShowError(r is null ? "Fan servisine ulaşılamadı." : r.Ok ? null : GraphicsMessage(r.Code, r.Error));
                RenderGraphics();
            };
            _gpuButtons[mode] = b;
            panel.Children.Add(b);
        }
        return panel;
    }

    private void RenderGraphics()
    {
        var s = _connection.Connected ? _connection.Status : null;
        var sw = s?.GraphicsSwitch;
        bool busy = sw?.Busy == true;
        GraphicsMode? shown = busy ? sw!.Target : s?.GraphicsMode;
        _gpuNow.Text = s is null ? "Fan hizmetine bağlanılamıyor; mod okunamadı."
            : s.GraphicsMode is not { } mode ? "Bu bilgisayar ekran kartı modunu bildirmiyor."
            : busy ? $"{GraphicsName(sw!.Target!.Value)} moduna geçiliyor, birkaç saniye sürer…"
            : $"Şu an: {GraphicsName(mode)}";
        _rendering = true;
        foreach (var (m, b) in _gpuButtons)
        {
            b.IsChecked = m == shown;
            b.IsEnabled = s?.GraphicsMode is not null && !busy && m != GraphicsMode.Discrete;
        }
        _rendering = false;
        if (busy) ShowError(null);
        else if (sw?.ErrorCode is { } code) ShowError(GraphicsMessage(code, sw.Error));
        _gpuRestart.Visibility = sw?.RestartAdvised == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string? text)
    {
        _gpuError.Text = text ?? "";
        _gpuError.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string GraphicsMessage(string? code, string? error) => code switch
    {
        "graphics_hdmi" => "HDMI kablosu takılıyken Dahili moda geçilemez: HDMI çıkışı NVIDIA kartına bağlı. Kabloyu çıkarıp yeniden deneyin.",
        "graphics_discrete" => "Bilgisayar Harici modda açıldı; önce Hibrit'i seçip yeniden başlatın.",
        "graphics_devices" => "NVIDIA ekran kartı açılıp kapatılamadı; mod değişmedi.",
        "graphics_not_set" => "BIOS Dahili modu kabul etmedi; NVIDIA kartı yeniden açıldı.",
        "graphics_busy" => "Bir mod değişikliği zaten sürüyor.",
        "graphics_unsupported" => "Bu mod şimdilik kullanılamıyor.",
        _ => error is null ? "Mod değiştirilemedi." : $"Mod değiştirilemedi ({error}).",
    };

    private static string GraphicsName(GraphicsMode mode) => mode switch
    {
        GraphicsMode.Integrated => "Dahili (yalnız Intel)",
        GraphicsMode.Discrete => "Harici (yalnız NVIDIA)",
        _ => "Hibrit (iki ekran kartı)",
    };

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
}
