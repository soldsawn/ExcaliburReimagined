using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Excalibur.App.Tray;
using Excalibur.Core.Battery;
using Excalibur.Core.Settings;
using Excalibur.Core.Fan.Ipc;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Pil: the charge and state as Windows knows them, and the battery's health as the service last read it
/// (at start and once a day; never in Windows' first minutes), and on battery the average power over the last read interval.
/// The app itself reads nothing from the battery.
/// </summary>
internal sealed class BatteryPage : UserControl
{
    private static readonly CultureInfo Tr = new("tr-TR");

    private const string CapacityTip = "İlk sayı: pil şu an tam dolduğunda tutabildiği enerji. İkinci sayı: yeniyken tutması gereken enerji. " +
        "Pil yaşlandıkça ilk sayı azalır.";

    private readonly FanConnection _connection;
    private readonly TextBlock _percent, _state, _remaining, _average, _interval;
    private readonly TextBlock _health, _capacity, _kind, _healthNote;
    private readonly StackPanel _healthValues, _pages;
    private readonly TextBlock _missing;
    private readonly Grid _table = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _tableEmpty, _tableNote;
    private string _tableKey = "";

    /// <param name="openSettings">Shows the Settings page, where the read interval is changed.</param>
    public BatteryPage(FanConnection connection, Action openSettings)
    {
        _connection = connection;
        _average = InfoPage.Text("", "Body", top: 8);
        _interval = InfoPage.Text("", "Caption", top: 4);
        var change = new Hyperlink(new Run("değiştir"));
        change.Click += (_, _) => openSettings();
        change.SetResourceReference(TextElement.ForegroundProperty, "Accent");
        _intervalText = new Run();
        _interval.Inlines.Add(_intervalText);
        _interval.Inlines.Add(new Run("   ·   "));
        _interval.Inlines.Add(change);
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
        _tableEmpty = InfoPage.Text("Pilde kullandıkça burada her güç modu ve fan profili için ortalama tüketim ve tam pille ne kadar " +
                                    "gidileceği görünür. Bir tahmin için o ayarla en az 30 dakika pil verisi gerekir.", "Caption");
        _tableNote = InfoPage.Text("", "Caption", top: 10);
        for (int c = 0; c < 4; c++)
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(150) });

        _pages = new StackPanel
        {
            Children =
            {
                InfoPage.Card("Şu an", _percent, _state, _remaining, _average, _interval),
                InfoPage.Card("Pil sağlığı", _healthValues, _healthNote),
                InfoPage.Card("Pil süresi tahmini", _tableEmpty, _table, _tableNote),
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
        Loaded += async (_, _) => { await _connection.RefreshSettingsAsync(); Render(); };
    }

    private readonly Run _intervalText;

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
        bool onBattery = b.State == BatteryState.OnBattery;
        _average.Visibility = _interval.Visibility = onBattery ? Visibility.Visible : Visibility.Collapsed;
        _average.Text = b.AverageWatts is { } w && b.AverageMinutes is { } am
            ? $"Son {am} dk ortalama: {w.ToString("0.0", Tr)} W"
            : "Ortalama güç ölçülüyor…";
        int minutes = _connection.Settings?.BatteryReadMinutes ?? BatteryRecorder.DefaultMinutes;
        _intervalText.Text = $"Okuma sıklığı: {minutes} dk";

        RenderTable(b);

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

    /// <summary>Rebuilt only when a row changes (the status comes every second).</summary>
    private void RenderTable(BatteryStatusDto b)
    {
        var rows = b.Estimates ?? [];
        string key = string.Join(";", rows.Select(e => $"{e.Mode}/{e.Profile}/{e.Minutes:F0}/{e.Watts:F1}")) + "|" + b.FullMWh;
        if (key == _tableKey) return;
        _tableKey = key;

        _tableEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _table.Visibility = _tableNote.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        AddRow(0, "Caption", "Güç modu · Fan profili", "Ortalama", "Tam pille", "Veri");
        int row = 1;
        foreach (var e in rows)
        {
            string name = $"{(e.Mode is { } m ? Texts.PowerMode(m) : "?")} · {ProfileNames.Label(e.Profile)}";
            bool enough = e.Minutes >= BatteryEstimates.NeededMinutes;
            string average = enough ? $"{e.Watts.ToString("0.0", Tr)} W" : "–";
            string full = enough && b.FullMWh is { } f && e.Hours(f) is { } h ? Short(TimeSpan.FromHours(h)) : "–";
            string data = enough ? Short(TimeSpan.FromMinutes(e.Minutes))
                : $"toplanıyor ({(int)e.Minutes}/{(int)BatteryEstimates.NeededMinutes} dk)";
            AddRow(row++, "Body", name, average, full, data);
        }
        _tableNote.Text = "Yalnız pilde geçen ve bu sırada ayarı değişmeyen süreler sayılır. \"Tam pille\", pilin şu an tam dolumda tuttuğu enerjiye göre" +
                          (b.FullMWh is { } fm ? $" ({Wh(fm)})." : ".");
    }

    private void AddRow(int row, string style, params string[] cells)
    {
        _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < cells.Length; c++)
        {
            var t = InfoPage.Text(cells[c], style, bottom: 6);
            Grid.SetRow(t, row);
            Grid.SetColumn(t, c);
            _table.Children.Add(t);
        }
    }

    /// <summary>"4 sa 15 dk", "45 dk".</summary>
    private static string Short(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} sa {t.Minutes} dk" : $"{(int)Math.Round(t.TotalMinutes)} dk";

    /// <summary>The Settings card that sets how often the battery is read on battery.</summary>
    internal static Border IntervalCard(FanConnection connection)
    {
        var combo = new ComboBox { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140 };
        combo.SetResourceReference(StyleProperty, "ThemedCombo");
        combo.ItemsSource = BatteryRecorder.IntervalChoices.Select(m => $"{m} dakika").ToList();
        var error = InfoPage.Text("", "Caption", top: 6);
        error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        error.Visibility = Visibility.Collapsed;
        bool loading = true;
        void Load()
        {
            loading = true;
            int current = connection.Settings?.BatteryReadMinutes ?? BatteryRecorder.DefaultMinutes;
            combo.SelectedIndex = Math.Max(0, BatteryRecorder.IntervalChoices.ToList().IndexOf(current));
            loading = false;
        }
        Load();
        combo.Loaded += async (_, _) => { await connection.RefreshSettingsAsync(); Load(); };
        combo.SelectionChanged += async (_, _) =>
        {
            if (loading || combo.SelectedIndex < 0) return;
            int minutes = BatteryRecorder.IntervalChoices[combo.SelectedIndex];
            var r = await connection.RequestAsync(new FanRequest("updateSettings", Settings: new ServiceSettingsPatch(BatteryReadMinutes: minutes)));
            error.Text = r is null ? "Arka plan hizmetine ulaşılamadı." : r.Ok ? "" : "Ayar kaydedilemedi.";
            error.Visibility = error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        };
        return InfoPage.Card("Pil",
            InfoPage.Text("Pildeyken ortalama gücü ölçmek için pil ne sıklıkla okunsun. Seyrek okuma yeterlidir ve dizüstüne daha az iş yükler.", "Caption"),
            combo, error);
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
