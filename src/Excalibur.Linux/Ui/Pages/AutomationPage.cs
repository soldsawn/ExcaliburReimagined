using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Excalibur.App.Tray;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.Linux.Ui.Pages;

/// <summary>
/// Otomasyon, as on Windows: what changes by itself when the charger is plugged in or out (off until the user switches it on);
/// every change is saved as soon as it is made. (Windows also has rules for running programs; not on Linux yet.)
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
        _status = Kit.Text("", "Caption", top: 10);
        _error = Kit.Text("", "Caption", top: 6).Res(TextBlock.ForegroundProperty, "Bad");
        _error.IsVisible = false;

        _acMode.ItemsSource = new[] { Keep, "Ofis", "Oyun", "Yüksek performans" };
        var batteryMode = Kit.Text("Ofis (pilde yalnız bu açılır)", "Body");

        var recommended = Kit.Button("Önerilen kuralları uygula", "AccentButton");
        recommended.Margin = new Thickness(0, 14, 0, 0);
        recommended.HorizontalAlignment = HorizontalAlignment.Left;
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

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0), ColumnDefinitions = new ColumnDefinitions("170,*,*") };
        AddRow(grid, 0, "", Kit.Text("Güç modu", "Kicker"), Kit.Text("Fan profili", "Kicker"));
        AddRow(grid, 1, "Prize takılınca", _acMode, _acProfile);
        AddRow(grid, 2, "Pile geçince", batteryMode, _batteryProfile);

        _enabled.IsCheckedChanged += async (_, _) => { if (!_loading) await SaveAsync(); };
        foreach (var c in new[] { _acMode, _acProfile, _batteryProfile })
            c.SelectionChanged += async (_, _) => { if (!_loading) await SaveAsync(); };

        Content = new StackPanel
        {
            Children =
            {
                Kit.Text("Otomasyon", "PageTitle"),
                Kit.Text("Siz bir şey yapmadan değişen ayarlar.", "Caption", top: 2, bottom: 18),
                Kit.Card("Priz ve pil",
                    Kit.Text("Şarj kablosu takılıp çıkarılınca güç modunu ve fan profilini kendiliğinden değiştirir. Boş bıraktığınız ayara dokunulmaz.", "Caption", bottom: 10),
                    _enabled, grid, recommended, _status, _error),
            },
        };

        _connection.Catalog.Changed += () => _ = LoadAsync();
        AttachedToVisualTree += async (_, _) => await LoadAsync();
    }

    private static ComboBox Combo() =>
        new ComboBox { Margin = new Thickness(0, 0, 12, 8), HorizontalAlignment = HorizontalAlignment.Left }.Classed("ThemedCombo");

    private static void AddRow(Grid grid, int row, string label, Control first, Control second)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var l = Kit.Text(label, "Body");
        l.VerticalAlignment = VerticalAlignment.Center;
        foreach (var (e, col) in new[] { ((Control)l, 0), (first, 1), (second, 2) })
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            grid.Children.Add(e);
        }
    }

    private IEnumerable<string> ProfileItems() =>
        new[] { Keep }.Concat(_connection.Catalog.Profiles.Select(p => ProfileNames.Label(p.Name)));

    private string? NameOf(object? item) =>
        item as string is { } text && text != Keep
            ? _connection.Catalog.Profiles.FirstOrDefault(p => ProfileNames.Label(p.Name) == text)?.Name
            : null;

    private static void SelectProfile(ComboBox box, string? name) =>
        box.SelectedItem = name is null ? Keep : ProfileNames.Label(name);

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
                Mode = (_acMode.SelectedItem as string) switch
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
        _error.Text = r is null ? "Fan servisine ulaşılamadı." : !r.Ok ? "Kural kaydedilemedi: " + r.Error : "";
        _error.IsVisible = _error.Text.Length > 0;
        if (r is { Ok: true })
            _status.Text = automation.Enabled ? "Kurallar açık." : "Kurallar kapalı: hiçbir şey kendiliğinden değişmez.";
    }
}
