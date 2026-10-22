using System.Windows;
using System.Windows.Controls;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Power;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Performans: the laptop's power mode (the graphics card's power limit). The service decides what is
/// allowed: on battery or a weak USB-C adapter only Office, so the other two are greyed out then.
/// </summary>
internal sealed class PowerPage : UserControl
{
    private static readonly (PowerMode Mode, string Label, string Detail)[] Modes =
    [
        (PowerMode.Office, "Ofis", "75 W · sessiz, az güç harcar"),
        (PowerMode.Gaming, "Oyun", "85 W · oyun ve ağır işler için"),
        (PowerMode.HighPerformance, "Yüksek performans", "100 W · en yüksek güç, en çok ısı"),
    ];

    private readonly FanConnection _connection;
    private readonly Dictionary<PowerMode, RadioButton> _buttons = [];
    private readonly TextBlock _now, _note, _error, _cpuWatts, _cpuLimits;
    private bool _rendering;

    public PowerPage(FanConnection connection)
    {
        _connection = connection;
        _cpuWatts = InfoPage.Text("–", "BigValue");
        _cpuWatts.SetResourceReference(TextBlock.ForegroundProperty, "Value");
        _cpuLimits = InfoPage.Text("", "Caption", top: 4);
        _now = InfoPage.Text("", "Body");
        _note = InfoPage.Text("", "Caption", top: 8);
        _error = InfoPage.Text("", "Caption", top: 6);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        _error.Visibility = Visibility.Collapsed;

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        string group = "powermode" + GetHashCode();
        foreach (var (mode, label, detail) in Modes)
        {
            var b = new RadioButton { GroupName = group, Margin = new Thickness(0, 0, 8, 8), MinWidth = 210, Height = 58 };
            b.Content = new StackPanel
            {
                Children = { new TextBlock { Text = label, FontWeight = FontWeights.SemiBold }, InfoPage.Text(detail, "Caption", top: 2) },
            };
            b.SetResourceReference(StyleProperty, "Segment");
            b.Checked += async (_, _) =>
            {
                if (_rendering) return;
                var r = await _connection.RequestAsync(new FanRequest("setPowerMode", PowerMode: mode));
                Show(r is null ? "Fan servisine ulaşılamadı." : r.Ok ? null : Message(r.Code, r.Error));
                Render();
            };
            _buttons[mode] = b;
            buttons.Children.Add(b);
        }

        var card = InfoPage.Card("Güç modu",
            InfoPage.Text("Ekran kartının ve işlemcinin çekebileceği gücü belirler. Daha yüksek mod daha hızlı ama daha sıcak ve daha gürültülüdür.", "Caption"),
            buttons, _now, _note, _error);
        Content = new StackPanel
        {
            Children =
            {
                InfoPage.Text("Performans", "PageTitle"),
                InfoPage.Text("Dizüstünün güç ayarı: Ofis, Oyun ve Yüksek performans.", "Caption", top: 2, bottom: 18),
                card,
                InfoPage.Card("İşlemci gücü", _cpuWatts, _cpuLimits,
                    InfoPage.Text("Güç modu işlemcinin uzun süre çekebileceği gücü de belirler: Ofis 35 W, Oyun 45 W, Yüksek performans 55 W.", "Caption", top: 6)),
                InfoPage.Card("Yakında",
                    InfoPage.Text("İşlemcinin çekebileceği gücü kendiniz sınırlama (daha sessiz ve serin çalışma için).", "Caption")),
            },
        };

        _connection.Changed += Render;
        Render();
    }

    private static string Message(string? code, string? error) => code switch
    {
        "power_battery" => "Pilde yalnızca Ofis modu kullanılabilir.",
        "power_usbc" => "Bu USB-C adaptörde yalnızca Ofis modu kullanılabilir.",
        "power_failed" => "Güç modu değiştirilemedi.",
        _ => error ?? "İstek kabul edilmedi.",
    };

    private void Show(string? error)
    {
        _error.Text = error ?? "";
        _error.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Render()
    {
        var power = _connection.Connected ? _connection.Status?.Power : null;
        _rendering = true;
        foreach (var (mode, button) in _buttons)
        {
            bool allowed = power is not null && (mode == PowerMode.Office || power.Lock == PowerLock.None);
            button.IsEnabled = allowed;
            button.IsChecked = power?.Mode == mode;
        }
        _rendering = false;

        _cpuWatts.Text = power?.CpuWatts is { } cw ? $"{cw} W" : "–";
        _cpuLimits.Text = power is null ? ""
            : power.CpuPl1Watts is { } pl1
                ? $"Uzun süreli sınır {pl1} W" + (power.CpuPl2Watts is { } pl2 ? $" · kısa süreli en çok {pl2} W" : "")
                : "İşlemci gücü okunamıyor.";

        if (power is null)
        {
            _now.Text = "Fan servisine bağlanılamıyor.";
            _note.Text = "";
            return;
        }
        string source = power.OnBattery ? $"pilde (%{power.BatteryPercent})" : power.UsbCAdapter ? "USB-C adaptörde" : "prizde";
        _now.Text = power.Mode is { } m
            ? $"Şu an: {Modes.First(x => x.Mode == m).Label}" + (power.GpuWatts is { } w ? $" ({w} W)" : "") + $" · {source}"
            : $"Şu anki mod okunamadı · {source}";
        _note.Text = power.Lock switch
        {
            PowerLock.OnBattery => "Pilde bilgisayar kendiliğinden Ofis moduna geçer. Prize takınca seçtiğiniz mod geri gelir.",
            PowerLock.UsbCAdapter => "Bu USB-C adaptör yeterince güçlü değil; yalnız Ofis modu açık. Prize takınca seçtiğiniz mod geri gelir.",
            _ => power.Preferred is { } p ? $"Prizdeyken seçtiğiniz mod: {Modes.First(x => x.Mode == p).Label}." : "",
        };
    }
}
