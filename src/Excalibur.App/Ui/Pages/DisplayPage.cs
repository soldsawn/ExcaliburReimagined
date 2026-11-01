using System.Windows;
using System.Windows.Controls;
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
    private readonly FanConnection _connection;
    private bool _loading;

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
                    InfoPage.Text("• Hibrit: iki ekran kartı birlikte; NVIDIA kartı yalnız gerektiğinde çalışır. Önerilen.", "Caption"),
                    InfoPage.Text("• Dahili: yalnız Intel; NVIDIA kartı kapanır, pil daha uzun gider. HDMI çıkışı çalışmaz.", "Caption", top: 2),
                    InfoPage.Text("• Harici: yalnız NVIDIA; oyunlarda en yüksek hız, pil daha kısa. Yeniden başlatma gerekir.", "Caption", top: 2),
                    InfoPage.Text("Modu buradan değiştirmek yakında gelecek.", "Caption", top: 8)),
            },
        };

        _connection.Changed += RenderGraphics;
        RenderGraphics();
    }

    private void RenderGraphics()
    {
        var s = _connection.Status;
        _gpuNow.Text = !_connection.Connected || s is null ? "Fan hizmetine bağlanılamıyor; mod okunamadı."
            : s.GraphicsMode is not { } mode ? "Bu bilgisayar ekran kartı modunu bildirmiyor."
            : $"Şu an: {GraphicsName(mode)}" + (s.HdmiConnected == true ? " · HDMI kablosu takılı" : "");
    }

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
