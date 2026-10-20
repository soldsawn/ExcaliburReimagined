using System.Windows;
using System.Windows.Controls;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Otomasyon: what changes by itself when the charger is plugged in or out (docs/08 1.4). Off until the
/// user switches it on; every change is saved as soon as it is made, like the rest of the app.
/// </summary>
internal sealed class AutomationPage : UserControl
{
    private const string Keep = "Değiştirme";

    private readonly FanConnection _connection;
    private readonly CheckBox _enabled = new() { Content = "Priz ve pil kurallarını kullan", FontSize = 14 };
    private readonly ComboBox _acMode, _acProfile, _batteryProfile;
    private readonly TextBlock _status, _error;
    private bool _loading;

    public AutomationPage(FanConnection connection)
    {
        _connection = connection;
        _acMode = Combo();
        _acProfile = Combo();
        _batteryProfile = Combo();
        _status = InfoPage.Text("", "Caption", top: 10);
        _error = InfoPage.Text("", "Caption", top: 6);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        _error.Visibility = Visibility.Collapsed;
        _enabled.SetResourceReference(Control.ForegroundProperty, "Text");

        _acMode.ItemsSource = new[] { Keep, "Ofis", "Oyun", "Yüksek performans" };
        var batteryMode = InfoPage.Text("Ofis (pilde yalnız bu açılır)", "Body");

        var recommended = new Button { Content = "Önerilen kuralları uygula", Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        recommended.SetResourceReference(StyleProperty, "AccentButton");
        recommended.Click += async (_, _) =>
        {
            _loading = true;
            _enabled.IsChecked = true;
            _acMode.SelectedItem = "Oyun";
            SelectProfile(_acProfile, "Balanced");
            SelectProfile(_batteryProfile, "Quiet");
            _loading = false;
            await SaveAsync();
        };

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(grid, 0, "", InfoPage.Text("Güç modu", "Kicker"), InfoPage.Text("Fan profili", "Kicker"));
        AddRow(grid, 1, "Prize takılınca", _acMode, _acProfile);
        AddRow(grid, 2, "Pile geçince", batteryMode, _batteryProfile);

        _enabled.Checked += async (_, _) => { if (!_loading) await SaveAsync(); };
        _enabled.Unchecked += async (_, _) => { if (!_loading) await SaveAsync(); };
        foreach (var c in new[] { _acMode, _acProfile, _batteryProfile })
            c.SelectionChanged += async (_, _) => { if (!_loading) await SaveAsync(); };

        Content = new StackPanel
        {
            Children =
            {
                InfoPage.Text("Otomasyon", "PageTitle"),
                InfoPage.Text("Siz bir şey yapmadan değişen ayarlar.", "Caption", top: 2, bottom: 18),
                InfoPage.Card("Priz ve pil",
                    InfoPage.Text("Şarj kablosu takılıp çıkarılınca güç modunu ve fan profilini kendiliğinden değiştirir. Boş bıraktığınız ayara dokunulmaz.", "Caption", bottom: 10),
                    _enabled, grid, recommended, _status, _error),
                InfoPage.Card("Yakında",
                    InfoPage.Text("• Bir oyun ya da uygulama açılınca profili değiştirme, kapanınca geri alma", "Caption"),
                    InfoPage.Text("• Kısayol tuşlarıyla profil değiştirme", "Caption", top: 4),
                    InfoPage.Text("• Casper'ın arka planda çalışan programlarını kapatma (istenince geri açılabilir)", "Caption", top: 4)),
            },
        };

        _connection.Catalog.Changed += () => _ = LoadAsync();
        Loaded += async (_, _) => await LoadAsync();
    }

    private static ComboBox Combo()
    {
        var c = new ComboBox { Margin = new Thickness(0, 0, 12, 8), HorizontalAlignment = HorizontalAlignment.Left };
        c.SetResourceReference(StyleProperty, "ThemedCombo");
        return c;
    }

    private static void AddRow(Grid grid, int row, string label, UIElement first, UIElement second)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = InfoPage.Text(label, "Body");
        l.VerticalAlignment = VerticalAlignment.Center;
        foreach (var (e, col) in new[] { ((UIElement)l, 0), (first, 1), (second, 2) })
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            grid.Children.Add(e);
        }
    }

    private IEnumerable<string> ProfileItems() =>
        new[] { Keep }.Concat(_connection.Catalog.Profiles.Select(p => Tray.ProfileNames.Label(p.Name)));

    private string? NameOf(object? item) =>
        item as string is { } text && text != Keep
            ? _connection.Catalog.Profiles.FirstOrDefault(p => Tray.ProfileNames.Label(p.Name) == text)?.Name
            : null;

    private void SelectProfile(ComboBox box, string? name) =>
        box.SelectedItem = name is null ? Keep : Tray.ProfileNames.Label(name);

    private async Task LoadAsync()
    {
        await _connection.RefreshSettingsAsync();
        if (_connection.Settings is not { } s)
        {
            _status.Text = "Fan servisine bağlanılamıyor.";
            return;
        }
        _loading = true;
        _acProfile.ItemsSource = ProfileItems().ToList();
        _batteryProfile.ItemsSource = ProfileItems().ToList();
        var a = s.PowerAutomation;
        _enabled.IsChecked = a.Enabled;
        _acMode.SelectedItem = a.OnAc.Mode switch
        {
            PowerMode.Office => "Ofis",
            PowerMode.Gaming => "Oyun",
            PowerMode.HighPerformance => "Yüksek performans",
            _ => Keep,
        };
        SelectProfile(_acProfile, a.OnAc.FanProfile);
        SelectProfile(_batteryProfile, a.OnBattery.FanProfile);
        _loading = false;
        _status.Text = a.Enabled ? "Kurallar açık." : "Kurallar kapalı: hiçbir şey kendiliğinden değişmez.";
    }

    private async Task SaveAsync()
    {
        var automation = new PowerAutomation
        {
            Enabled = _enabled.IsChecked == true,
            OnAc = new PowerRule
            {
                Mode = _acMode.SelectedItem as string switch
                {
                    "Ofis" => PowerMode.Office,
                    "Oyun" => PowerMode.Gaming,
                    "Yüksek performans" => PowerMode.HighPerformance,
                    _ => null,
                },
                FanProfile = NameOf(_acProfile.SelectedItem),
            },
            OnBattery = new PowerRule { Mode = PowerMode.Office, FanProfile = NameOf(_batteryProfile.SelectedItem) },
        };
        var r = await _connection.RequestAsync(new FanRequest("updateSettings", Settings: new ServiceSettingsPatch(PowerAutomation: automation)));
        if (r is null) _error.Text = "Fan servisine ulaşılamadı.";
        else if (!r.Ok) _error.Text = "Kural kaydedilemedi: " + r.Error;
        _error.Visibility = string.IsNullOrEmpty(_error.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (r is { Ok: true })
        {
            _error.Text = "";
            _error.Visibility = Visibility.Collapsed;
            _status.Text = automation.Enabled ? "Kurallar açık." : "Kurallar kapalı: hiçbir şey kendiliğinden değişmez.";
        }
    }
}
