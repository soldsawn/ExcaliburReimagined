using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Excalibur.Core.Battery;
using Excalibur.Core.Fan.Ipc;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Pil: the charge and state as Windows knows them, and the battery's health as the service last read it
/// (at start and once a day; never in Windows' first minutes). The app itself reads nothing from the battery.
/// </summary>
internal sealed class BatteryPage : UserControl
{
    private static readonly CultureInfo Tr = new("tr-TR");

    private const string CapacityTip = "İlk sayı: pil şu an tam dolduğunda tutabildiği enerji. İkinci sayı: yeniyken tutması gereken enerji. " +
        "Pil yaşlandıkça ilk sayı azalır.";

    private readonly FanConnection _connection;
    private readonly TextBlock _percent, _state, _remaining;
    private readonly TextBlock _health, _capacity, _kind, _healthNote;
    private readonly StackPanel _healthValues, _pages;
    private readonly TextBlock _missing;

    public BatteryPage(FanConnection connection)
    {
        _connection = connection;
        _percent = InfoPage.Text("–", "BigValue");
        _state = InfoPage.Text("", "Body", top: 2);
        _remaining = InfoPage.Text("", "Caption", top: 6);
        _health = InfoPage.Text("–", "BigValue");
        _capacity = InfoPage.Text("", "Body");
        _kind = InfoPage.Text("", "Caption", top: 6);
        _healthNote = InfoPage.Text("", "Caption", top: 6);
        _healthValues = new StackPanel
        {
            Children =
            {
                _health,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0),
                    Children = { _capacity, InfoPage.Tip(CapacityTip) },
                },
                _kind,
            },
        };
        _missing = InfoPage.Text("", "Body");

        _pages = new StackPanel
        {
            Children =
            {
                InfoPage.Card("Şu an", _percent, _state, _remaining),
                InfoPage.Card("Pil sağlığı", _healthValues, _healthNote),
                InfoPage.Card("Yakında",
                    InfoPage.Text("• Son dakikalardaki ortalama güç tüketimi", "Caption"),
                    InfoPage.Text("• Güç modu ve fan profiline göre tam pille ne kadar gidileceği", "Caption", top: 4)),
            },
        };

        Content = new StackPanel
        {
            Children =
            {
                InfoPage.Text("Pil", "PageTitle"),
                InfoPage.Text("Pilin şu anki durumu ve sağlığı.", "Caption", top: 2, bottom: 18),
                _missing,
                _pages,
            },
        };

        _connection.Changed += Render;
        Render();
    }

    private void Render()
    {
        var b = _connection.Status?.Battery;
        bool known = b is not null;
        _pages.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        _missing.Visibility = known ? Visibility.Collapsed : Visibility.Visible;
        if (b is null)
        {
            _missing.Text = _connection.Status is null
                ? "Pil bilgisi için arka plan hizmetine bağlanılıyor…"
                : "Pil bilgisi alınamadı: bu bilgisayarda pil bulunamadı ya da arka plan hizmeti güncel değil.";
            return;
        }

        _percent.Text = b.Percent is { } p ? $"%{p}" : "–";
        _state.Text = b.State switch
        {
            BatteryState.OnBattery => "Pilde",
            BatteryState.Charging => "Şarj oluyor",
            BatteryState.Full => "Dolu",
            _ => "Prizde, şarj olmuyor",
        };
        _remaining.Text = b.RemainingMinutes is { } m ? $"Kalan süre: {Texts.Duration(TimeSpan.FromMinutes(m))} (Windows'un tahmini)" : "";
        _remaining.Visibility = b.RemainingMinutes is null ? Visibility.Collapsed : Visibility.Visible;

        bool read = b.FullMWh is not null && b.DesignedMWh is not null;
        _healthValues.Visibility = read ? Visibility.Visible : Visibility.Collapsed;
        if (!read)
        {
            _healthNote.Text = "Pil sağlığı Windows açıldıktan birkaç dakika sonra okunur.";
            _healthNote.Visibility = Visibility.Visible;
            return;
        }
        _health.Text = b.HealthPercent is { } h ? $"%{h}" : "–";
        _capacity.Text = $"{Wh(b.FullMWh!.Value)} / {Wh(b.DesignedMWh!.Value)}";
        _kind.Text = string.Join("   ·   ", new[] { Chemistry(b.Chemistry), b.Cycles is { } c ? $"{c} şarj döngüsü" : null }.OfType<string>());
        _kind.Visibility = _kind.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _healthNote.Text = b.FullMWh > b.DesignedMWh
            ? "Pil şu an yeniyken tutması gerekenden biraz fazlasını tutuyor; yeni pillerde olağandır."
            : "";
        _healthNote.Visibility = _healthNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Wh(int mWh) => (mWh / 1000.0).ToString("0.0", Tr) + " Wh";

    private static string? Chemistry(string? code) => code?.ToUpperInvariant() switch
    {
        null or "" => null,
        "LION" or "LI-I" or "LI-ION" => "Lityum iyon pil",
        "LIP" or "LIPO" or "LI-P" => "Lityum polimer pil",
        "NIMH" => "Nikel metal hidrit pil",
        "NICD" => "Nikel kadmiyum pil",
        "PBAC" => "Kurşun asit pil",
        _ => $"Pil türü: {code}",
    };
}
