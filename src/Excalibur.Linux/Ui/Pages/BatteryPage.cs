using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Excalibur.App;
using Excalibur.App.Tray;
using Excalibur.Core.Battery;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Settings;
using Excalibur.Linux.Ui.Controls;

namespace Excalibur.Linux.Ui.Pages;

/// <summary>
/// Pil, as on Windows: the charge and state, the battery's health as the service last read it, the average power on battery,
/// the battery-life estimates per power mode and fan profile, and what the fan setting costs on battery.
/// </summary>
internal sealed class BatteryPage : UserControl
{
    private const string CapacityTip = "İlk sayı: pil şu an tam dolduğunda tutabildiği enerji. İkinci sayı: yeniyken tutması gereken enerji. " +
        "Pil yaşlandıkça ilk sayı azalır.";

    private readonly FanConnection _connection;
    private readonly TextBlock _percent, _state, _remaining, _average, _intervalText;
    private readonly StackPanel _interval;
    private readonly TextBlock _health, _capacity, _kind, _healthNote;
    private readonly StackPanel _healthValues, _pages;
    private readonly TextBlock _missing;
    private readonly Grid _table = new() { Margin = new Thickness(0, 10, 0, 0), ColumnDefinitions = new ColumnDefinitions("*,150,150,150") };
    private readonly TextBlock _tableEmpty, _tableNote, _fanEffect;
    private string _tableKey = "";
    private readonly Control _intervalCard;

    public BatteryPage(FanConnection connection)
    {
        _connection = connection;
        _average = Kit.Text("", "Body", top: 8);
        _intervalText = Kit.Text("", "Caption");
        // The read interval card is at the bottom of this page.
        var change = new TextBlock { Text = "değiştir", FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand) }.Res(TextBlock.ForegroundProperty, "Accent");
        change.PointerPressed += (_, _) => _intervalCard?.BringIntoView();
        _interval = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Children = { _intervalText, Kit.Text("   ·   ", "Caption"), change } };
        _percent = Kit.Text("–", "BigValue");
        _state = Kit.Text("", "Body", top: 2);
        _remaining = Kit.Text("", "Caption", top: 6);
        _health = Kit.Text("–", "BigValue");
        _capacity = Kit.Text("", "Body");
        _kind = Kit.Text("", "Caption", top: 6);
        _healthNote = Kit.Text("", "Caption", top: 6);
        _healthValues = new StackPanel
        {
            Children =
            {
                _health,
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0), Children = { _capacity, Kit.Tip(CapacityTip) } },
                _kind,
            },
        };
        _missing = Kit.Text("", "Body");
        _tableEmpty = Kit.Text("Pilde kullandıkça burada her güç modu ve fan profili için ortalama tüketim ve tam pille ne kadar " +
                               "gidileceği görünür. Bir tahmin için o ayarla en az 30 dakika pil verisi gerekir.", "Caption");
        _tableNote = Kit.Text("", "Caption", top: 10);
        _fanEffect = Kit.Text("", "Body");
        _intervalCard = IntervalCard(connection);

        _pages = new StackPanel
        {
            Children =
            {
                Kit.Card("Şu an", _percent, _state, _remaining, _average, _interval),
                Kit.Card("Pil sağlığı", _healthValues, _healthNote),
                Kit.Card("Pil süresi tahmini", _tableEmpty, _table, _tableNote),
                Kit.Card("Fan ayarının pile etkisi",
                    Kit.Text("Bilgisayar boştayken (işlemci az çalışırken) fanları uygulamanın yönetmesi, fanların orijinal ayarına göre pilden " +
                             "ne kadar fazla ya da az harcıyor.", "Caption", bottom: 8),
                    _fanEffect),
                _intervalCard,
            },
        };

        Content = new StackPanel
        {
            Children =
            {
                Kit.Text("Pil", "PageTitle"),
                Kit.Text("Pilin şu anki durumu ve sağlığı.", "Caption", top: 2, bottom: 18),
                _missing,
                _pages,
            },
        };

        _connection.Changed += Render;
        Render();
        AttachedToVisualTree += async (_, _) => { await _connection.RefreshSettingsAsync(); Render(); };
    }

    /// <summary>One decimal with a Turkish comma (the app runs with invariant globalization).</summary>
    private static string F1(double value) => value.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');

    private void Render()
    {
        var b = _connection.Status?.Battery;
        bool known = b is not null;
        _pages.IsVisible = known;
        _missing.IsVisible = !known;
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
        _remaining.Text = b.RemainingMinutes is { } m ? $"Kalan süre: {Texts.Duration(TimeSpan.FromMinutes(m))} (sistemin tahmini)" : "";
        _remaining.IsVisible = b.RemainingMinutes is not null;
        bool onBattery = b.State == BatteryState.OnBattery;
        _average.IsVisible = _interval.IsVisible = onBattery;
        _average.Text = b.AverageWatts is { } w && b.AverageMinutes is { } am
            ? $"Son {am} dk ortalama: {F1(w)} W"
            : "Ortalama güç ölçülüyor…";
        int minutes = _connection.Settings?.BatteryReadMinutes ?? BatteryRecorder.DefaultMinutes;
        _intervalText.Text = $"Okuma sıklığı: {minutes} dk";

        RenderTable(b);
        _fanEffect.Text = FanEffect(b);

        bool read = b.FullMWh is not null && b.DesignedMWh is not null;
        _healthValues.IsVisible = read;
        if (!read)
        {
            _healthNote.Text = "Pil sağlığı bilgisayar açıldıktan birkaç dakika sonra okunur.";
            _healthNote.IsVisible = true;
            return;
        }
        _health.Text = b.HealthPercent is { } h ? $"%{h}" : "–";
        _capacity.Text = $"{Wh(b.FullMWh!.Value)} / {Wh(b.DesignedMWh!.Value)}";
        _kind.Text = string.Join("   ·   ", new[] { Chemistry(b.Chemistry), b.Cycles is { } c ? $"{c} şarj döngüsü" : null }.OfType<string>());
        _kind.IsVisible = _kind.Text.Length > 0;
        _healthNote.Text = b.FullMWh > b.DesignedMWh
            ? "Pil şu an yeniyken tutması gerekenden biraz fazlasını tutuyor; yeni pillerde olağandır."
            : "";
        _healthNote.IsVisible = _healthNote.Text.Length > 0;
    }

    /// <summary>Rebuilt only when a row changes (the status comes every second).</summary>
    private void RenderTable(BatteryStatusDto b)
    {
        var rows = b.Estimates ?? [];
        string key = string.Join(";", rows.Select(e => $"{e.Mode}/{e.Profile}/{e.Minutes:F0}/{e.Watts:F1}")) + "|" + b.FullMWh;
        if (key == _tableKey) return;
        _tableKey = key;

        _tableEmpty.IsVisible = rows.Count == 0;
        _table.IsVisible = _tableNote.IsVisible = rows.Count > 0;
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        AddRow(0, "Caption", "Güç modu · Fan profili", "Ortalama", "Tam pille", "Veri");
        int row = 1;
        foreach (var e in rows)
        {
            string name = $"{(e.Mode is { } m ? Texts.PowerMode(m) : "?")} · {ProfileNames.Label(e.Profile)}";
            bool enough = e.Minutes >= BatteryEstimates.NeededMinutes;
            string average = enough ? $"{F1(e.Watts)} W" : "–";
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
        _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < cells.Length; c++)
        {
            var t = Kit.Text(cells[c], style, bottom: 6);
            Grid.SetRow(t, row);
            Grid.SetColumn(t, c);
            _table.Children.Add(t);
        }
    }

    /// <summary>Each profile's idle average against the EC's automatic control, once both have enough data.</summary>
    private static string FanEffect(BatteryStatusDto b)
    {
        var idle = b.IdleEstimates ?? [];
        var ec = idle.FirstOrDefault(e => e.Profile == "EcAuto");
        var profiles = idle.Where(e => e.Profile != "EcAuto").ToList();
        bool Enough(BatteryEstimate? e) => e is not null && e.Minutes >= BatteryEstimates.NeededMinutes;

        var ready = profiles.Where(Enough).ToList();
        if (Enough(ec) && ready.Count > 0)
            return string.Join(Environment.NewLine, ready.Select(p =>
            {
                double diff = p.Watts - ec!.Watts;
                if (Math.Abs(diff) < 0.3) return $"{ProfileNames.Label(p.Profile)}: fark yok denecek kadar az";
                string line = $"{ProfileNames.Label(p.Profile)}: orijinal ayara göre {F1(Math.Abs(diff))} W daha {(diff > 0 ? "fazla" : "az")}";
                if (b.FullMWh is { } f && p.Hours(f) is { } hp && ec.Hours(f) is { } he)
                    line += $" · tam pille {Short(TimeSpan.FromHours(Math.Abs(hp - he)))} daha {(hp < he ? "kısa" : "uzun")}";
                return line;
            }));

        var progress = new[] { ec ?? new BatteryEstimate(null, "EcAuto", 0, 0) }.Concat(profiles)
            .Select(e => $"{ProfileNames.Label(e.Profile)} {Math.Min((int)e.Minutes, (int)BatteryEstimates.NeededMinutes)}/{(int)BatteryEstimates.NeededMinutes} dk");
        return "Karşılaştırma için pilde, boştayken orijinal ayarla ve bir profille en az 30'ar dakika veri gerekir." +
               Environment.NewLine + "Şu an: " + string.Join("   ·   ", progress);
    }

    /// <summary>"4 sa 15 dk", "45 dk".</summary>
    private static string Short(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} sa {t.Minutes} dk" : $"{(int)Math.Round(t.TotalMinutes)} dk";

    /// <summary>The card that sets how often the battery is read on battery.</summary>
    internal static Control IntervalCard(FanConnection connection)
    {
        var combo = new ComboBox { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140 }.Classed("ThemedCombo");
        combo.ItemsSource = BatteryRecorder.IntervalChoices.Select(m => $"{m} dakika").ToList();
        var error = Kit.Text("", "Caption", top: 6).Res(TextBlock.ForegroundProperty, "Bad");
        error.IsVisible = false;
        bool loading = true;
        void Load()
        {
            loading = true;
            int current = connection.Settings?.BatteryReadMinutes ?? BatteryRecorder.DefaultMinutes;
            combo.SelectedIndex = Math.Max(0, BatteryRecorder.IntervalChoices.ToList().IndexOf(current));
            loading = false;
        }
        Load();
        combo.AttachedToVisualTree += async (_, _) => { await connection.RefreshSettingsAsync(); Load(); };
        combo.SelectionChanged += async (_, _) =>
        {
            if (loading || combo.SelectedIndex < 0) return;
            int minutes = BatteryRecorder.IntervalChoices[combo.SelectedIndex];
            var r = await connection.RequestAsync(new FanRequest("updateSettings", Settings: new ServiceSettingsPatch(BatteryReadMinutes: minutes)));
            error.Text = r is null ? "Arka plan hizmetine ulaşılamadı." : r.Ok ? "" : "Ayar kaydedilemedi.";
            error.IsVisible = error.Text.Length > 0;
        };
        return Kit.Card("Okuma sıklığı",
            Kit.Text("Pildeyken ortalama gücü ölçmek için pil ne sıklıkla okunsun. Seyrek okuma yeterlidir ve dizüstüne daha az iş yükler.", "Caption"),
            combo, error);
    }

    private static string Wh(int mWh) => F1(mWh / 1000.0) + " Wh";

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
