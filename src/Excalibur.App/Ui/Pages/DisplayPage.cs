using System.Windows;
using System.Windows.Controls;
using Excalibur.Core.Settings;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Ekran: the screen's refresh rate follows the charger (docs/08 3.1). The tray applies the rule; this page only
/// edits <see cref="UserSettings.RefreshRate"/>, which both processes share through the settings file.
/// </summary>
internal sealed class DisplayPage : UserControl
{
    private const string Highest = "En yüksek";

    private readonly UserSettingsStore _store = new();
    private readonly DisplayRefresh.Panel? _panel = DisplayRefresh.FindInternal();
    private readonly CheckBox _enabled = new() { Content = "Prizde yüksek, pilde düşük yenileme hızı kullan", FontSize = 14 };
    private readonly ComboBox _onAc, _onBattery;
    private readonly TextBlock _info;
    private bool _loading;

    public DisplayPage()
    {
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
                InfoPage.Card("Yakında",
                    InfoPage.Text("• Ekran kartı modu: karma, yalnız harici ya da yalnız dahili (değiştirmek için yeniden başlatma gerekir)", "Caption")),
            },
        };
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
}
