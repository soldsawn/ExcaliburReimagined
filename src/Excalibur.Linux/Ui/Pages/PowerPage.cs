using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Power;
using Excalibur.Linux.Ui.Controls;

namespace Excalibur.Linux.Ui.Pages;

/// <summary>
/// Performans, as on Windows: the power mode, the power the CPU and graphics card draw right now, and the user's own CPU and
/// graphics card limits. On Linux the power mode moves the CPU's limit and the laptop's own fan table but not the graphics
/// card's (the BIOS does that through ACPI, which user space cannot call), so the page says so and shows only the CPU part.
/// </summary>
internal sealed class PowerPage : UserControl
{
    private static readonly (PowerMode Mode, string Label)[] Modes =
    [
        (PowerMode.Office, "Ofis"), (PowerMode.Gaming, "Oyun"), (PowerMode.HighPerformance, "Yüksek performans"),
    ];

    private const string Pl1Tip = "İşlemcinin dakikalarca sürdürebileceği güç. Isıyı ve fan sesini en çok bu belirler. " +
        "Güç modu bunu ayarlar; her modun değeri mod düğmelerinde yazar.";
    private const string Pl2Tip = "İşlemcinin birkaç saniyelik ani yüklerde çıkabileceği en yüksek güç. Programlar daha çabuk açılır, " +
        "ama kısa süreli ısınmaya ve fanın birden hızlanmasına yol açar.";
    private const string GpuTip = "Ekran kartının çekebileceği en yüksek güç (sürücünün sınırı). Linux'ta güç modu bunu değiştirmez.";
    private const string GpuLimitTip = "Ekran kartı bu gücün üstüne çıkmasın diye hızı birkaç saniyede bir ayarlanır. " +
        "Boştayken etkisi yoktur; ağır oyunlarda sınıra kadar çıkar.";
    private const string NowTip = "Şu an çekilen güç. Boştayken düşüktür, oyunlarda ve ağır işlerde sınıra yaklaşır.";

    private readonly FanConnection _connection;
    private readonly Dictionary<PowerMode, RadioButton> _buttons = [];
    // What each mode allows on this laptop, as the service measured it.
    private readonly Dictionary<PowerMode, TextBlock> _modeWatts = [];
    private readonly TextBlock _now, _note, _error;
    private readonly TextBlock _cpuWatts, _cpuPl1, _cpuPl2, _gpuWatts, _gpuLimit;
    private readonly StackPanel _cpuPl2Row;
    private readonly BlockBar _cpuBar = new() { Margin = new Thickness(0, 6, 0, 0) }, _gpuBar = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly RadioButton _byMode, _own;
    private readonly Slider _pl1, _pl2;
    private readonly TextBlock _pl1Text, _pl2Text, _limitNote, _limitError;
    private readonly StackPanel _ownPanel;
    private readonly RadioButton _gpuByMode, _gpuOwn;
    private readonly Slider _gpuSlider;
    private readonly TextBlock _gpuText, _gpuNote, _gpuError;
    private readonly StackPanel _gpuPanel;
    private bool _rendering, _limitDirty, _gpuDirty;

    public PowerPage(FanConnection connection)
    {
        _connection = connection;
        _now = Kit.Text("", "Body");
        _note = Kit.Text("", "Caption", top: 8);
        _error = ErrorText();

        // ---- power mode ----
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        string group = "powermode" + GetHashCode();
        foreach (var (mode, label) in Modes)
        {
            var watts = Kit.Text("", "Caption", top: 2);
            _modeWatts[mode] = watts;
            var b = Kit.Segment(new StackPanel { Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, watts } }, group);
            b.Margin = new Thickness(0, 0, 8, 8);
            b.MinWidth = 210;
            b.Height = 58;
            b.IsCheckedChanged += async (_, _) =>
            {
                if (_rendering || b.IsChecked != true) return;
                var r = await _connection.RequestAsync(new FanRequest("setPowerMode", PowerMode: mode));
                Show(_error, r is null ? "Fan servisine ulaşılamadı." : r.Ok ? null : Message(r.Code, r.Error));
                Render();
            };
            _buttons[mode] = b;
            buttons.Children.Add(b);
        }

        // ---- power now ----
        _cpuWatts = Kit.Text("–", "BigValue");
        _gpuWatts = Kit.Text("–", "BigValue");
        _cpuBar.Res(BlockBar.FillProperty, "Value").Res(BlockBar.EmptyProperty, "Border");
        _gpuBar.Res(BlockBar.FillProperty, "Value").Res(BlockBar.EmptyProperty, "Border");
        var (pl1Row, pl1) = Term(Pl1Tip);
        var (pl2Row, pl2) = Term(Pl2Tip);
        var (gpuRow, gpuLimit) = Term(GpuTip);
        (_cpuPl1, _cpuPl2, _gpuLimit, _cpuPl2Row) = (pl1, pl2, gpuLimit, pl2Row);
        var cpuColumn = new StackPanel { Children = { Kit.Labeled("İŞLEMCİ", "Kicker", NowTip), _cpuWatts, _cpuBar, pl1Row, pl2Row } };
        var gpuColumn = new StackPanel { Children = { Kit.Labeled("EKRAN KARTI", "Kicker", NowTip), _gpuWatts, _gpuBar, gpuRow } };
        Grid.SetColumn(gpuColumn, 2);
        var meters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,28,*"), Children = { cpuColumn, gpuColumn } };

        // ---- own CPU limit ----
        string limitGroup = "cpulimit" + GetHashCode();
        _byMode = Choice(limitGroup, "Güç modu belirlesin", "önerilen");
        _own = Choice(limitGroup, "Kendim sınırlayacağım", "daha serin ve sessiz");
        _byMode.IsCheckedChanged += async (_, _) => { if (!_rendering && _byMode.IsChecked == true) await SendLimit(null); };
        _own.IsCheckedChanged += (_, _) => { if (!_rendering && _own.IsChecked == true) { _limitDirty = true; RenderLimit(); } };

        (_pl1, _pl1Text) = (LimitSlider(CpuLimit.MinPl1, CpuLimit.MaxPl1, 35), Kit.Text("", "Body"));
        (_pl2, _pl2Text) = (LimitSlider(CpuLimit.MinPl2, CpuLimit.MaxPl2, 65), Kit.Text("", "Body"));
        // The short-term limit can never be below the long-term one.
        _pl1.ValueChanged += (_, _) => { if (_pl2.Value < _pl1.Value) _pl2.Value = Math.Max(_pl1.Value, CpuLimit.MinPl2); SliderTexts(); };
        _pl2.ValueChanged += (_, _) => { if (_pl1.Value > _pl2.Value) _pl1.Value = _pl2.Value; SliderTexts(); };
        _limitNote = Kit.Text("", "Caption", top: 6);
        _limitError = ErrorText();
        var apply = Kit.Button("Uygula", "AccentButton");
        apply.Margin = new Thickness(0, 12, 0, 0);
        apply.HorizontalAlignment = HorizontalAlignment.Left;
        apply.MinWidth = 110;
        apply.Click += async (_, _) => await SendLimit(new CpuLimit((int)_pl1.Value, (int)_pl2.Value));
        _ownPanel = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children = { SliderRow("Uzun süreli sınır", Pl1Tip, _pl1, _pl1Text), SliderRow("Kısa süreli sınır", Pl2Tip, _pl2, _pl2Text), _limitNote, apply },
        };
        SliderTexts();

        // ---- own graphics card limit ----
        string gpuGroup = "gpulimit" + GetHashCode();
        _gpuByMode = Choice(gpuGroup, "Sürücü belirlesin", "önerilen");
        _gpuOwn = Choice(gpuGroup, "Kendim sınırlayacağım", "daha serin ve sessiz");
        _gpuByMode.IsCheckedChanged += async (_, _) => { if (!_rendering && _gpuByMode.IsChecked == true) await SendGpuLimit(null); };
        _gpuOwn.IsCheckedChanged += (_, _) => { if (!_rendering && _gpuOwn.IsChecked == true) { _gpuDirty = true; RenderGpuLimit(); } };
        _gpuSlider = new Slider
        {
            Minimum = GpuLimit.MinWatts, Maximum = 75, Value = 50, TickFrequency = GpuLimit.Step, IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0),
        }.Classed("ThinSlider");
        _gpuText = Kit.Text($"{_gpuSlider.Value:0} W", "Body");
        _gpuSlider.ValueChanged += (_, _) => { if (!_rendering) _gpuDirty = true; _gpuText.Text = $"{_gpuSlider.Value:0} W"; };
        _gpuNote = Kit.Text("", "Caption", top: 6);
        _gpuError = ErrorText();
        var gpuApply = Kit.Button("Uygula", "AccentButton");
        gpuApply.Margin = new Thickness(0, 12, 0, 0);
        gpuApply.HorizontalAlignment = HorizontalAlignment.Left;
        gpuApply.MinWidth = 110;
        gpuApply.Click += async (_, _) => await SendGpuLimit((int)_gpuSlider.Value);
        _gpuPanel = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children = { SliderRow("En yüksek güç", GpuLimitTip, _gpuSlider, _gpuText), _gpuNote, gpuApply },
        };

        Content = new StackPanel
        {
            Children =
            {
                Kit.Text("Performans", "PageTitle"),
                Kit.Text("Dizüstünün güç ayarı ve işlemci ile ekran kartının şu an çektiği güç.", "Caption", top: 2, bottom: 18),
                Kit.Card("Güç modu",
                    Kit.Text("İşlemcinin çekebileceği gücü ve dizüstünün kendi fan ayarını belirler. Daha yüksek mod daha hızlı ama daha sıcak ve daha gürültülüdür. " +
                             "Linux'ta güç modu ekran kartının sınırını değiştirmez; ekran kartını aşağıdaki sınırla kısabilirsiniz.", "Caption"),
                    buttons, _now, _note, _error),
                Kit.Card("Anlık güç", meters),
                SideBySide(
                    Kit.Card("İşlemci güç sınırı",
                        Kit.Text("İşlemcinin gücünü kendiniz belirleyebilirsiniz: düşük değer daha az ısı ve fan sesi, ağır işlerde biraz daha yavaş çalışma demektir. " +
                                 "Kendi sınırınız açıkken güç modu işlemciyi değiştirmez.", "Caption"),
                        new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Children = { _byMode, _own } },
                        _ownPanel, _limitError),
                    Kit.Card("Ekran kartı güç sınırı",
                        Kit.Text("Ekran kartının gücünü sürücünün izin verdiğinin altına indirebilirsiniz: daha az ısı ve fan sesi, oyunlarda biraz daha düşük kare hızı.", "Caption"),
                        new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Children = { _gpuByMode, _gpuOwn } },
                        _gpuPanel, _gpuError)),
            },
        };

        _connection.Changed += Render;
        Render();
    }

    // ---- building blocks -------------------------------------------------------------------

    private static TextBlock ErrorText()
    {
        var t = Kit.Text("", "Caption", top: 6).Res(TextBlock.ForegroundProperty, "Bad");
        t.IsVisible = false;
        return t;
    }

    /// <summary>Two cards in one row.</summary>
    private static Grid SideBySide(Control left, Control right)
    {
        Grid.SetColumn(right, 2);
        return new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,*"), Children = { left, right } };
    }

    /// <summary>A caption line whose text changes, followed by its (i).</summary>
    private static (StackPanel Row, TextBlock Text) Term(string tip)
    {
        var row = Kit.Labeled("", "Caption", tip, top: 6);
        return (row, (TextBlock)row.Children[0]);
    }

    private static RadioButton Choice(string group, string label, string detail)
    {
        var b = Kit.Segment(new StackPanel { Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, Kit.Text(detail, "Caption", top: 2) } }, group);
        b.Margin = new Thickness(0, 0, 8, 8);
        b.MinWidth = 180;
        b.Height = 58;
        return b;
    }

    private Slider LimitSlider(int min, int max, int value)
    {
        var s = new Slider
        {
            Minimum = min, Maximum = max, Value = value, TickFrequency = CpuLimit.Step, IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0),
        }.Classed("ThinSlider");
        s.ValueChanged += (_, _) => { if (!_rendering) _limitDirty = true; };
        return s;
    }

    private static Grid SliderRow(string label, string tip, Slider slider, TextBlock value)
    {
        var name = Kit.Labeled(label, "Body", tip);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        value.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { Margin = new Thickness(0, 6, 0, 0), ColumnDefinitions = new ColumnDefinitions("170,*,60"), Children = { name, slider, value } };
    }

    private void SliderTexts()
    {
        _pl1Text.Text = $"{_pl1.Value:0} W";
        _pl2Text.Text = $"{_pl2.Value:0} W";
    }

    // ---- actions ---------------------------------------------------------------------------

    private async Task SendLimit(CpuLimit? limit)
    {
        var r = await _connection.RequestAsync(new FanRequest("setCpuLimit", CpuLimit: limit));
        Show(_limitError, r is null ? "Fan servisine ulaşılamadı." : r.Ok ? null : r.Code switch
        {
            "cpu_limit_invalid" => "Bu değerler kullanılamaz.",
            "cpu_limit_failed" => "İşlemci sınırı yazılamadı.",
            _ => r.Error ?? "İstek kabul edilmedi.",
        });
        if (r?.Ok == true) _limitDirty = false;
        Render();
    }

    private async Task SendGpuLimit(int? watts)
    {
        var r = await _connection.RequestAsync(new FanRequest("setGpuLimit", GpuLimitWatts: watts));
        Show(_gpuError, r is null ? "Fan servisine ulaşılamadı." : r.Ok ? null : r.Code switch
        {
            "gpu_limit_invalid" => "Bu değer kullanılamaz.",
            "gpu_limit_failed" => "Ekran kartı sınırlanamadı.",
            _ => r.Error ?? "İstek kabul edilmedi.",
        });
        if (r?.Ok == true) _gpuDirty = false;
        Render();
    }

    private static string Message(string? code, string? error) => code switch
    {
        "power_battery" => "Pilde yalnızca Ofis modu kullanılabilir.",
        "power_usbc" => "Bu USB-C adaptörde yalnızca Ofis modu kullanılabilir.",
        "power_failed" => "Güç modu değiştirilemedi.",
        _ => error ?? "İstek kabul edilmedi.",
    };

    private static void Show(TextBlock target, string? error)
    {
        target.Text = error ?? "";
        target.IsVisible = error is not null;
    }

    // ---- rendering -------------------------------------------------------------------------

    private void Render()
    {
        var power = _connection.Connected ? _connection.Status?.Power : null;
        _rendering = true;
        foreach (var (mode, button) in _buttons)
        {
            button.IsEnabled = power is not null && (mode == PowerMode.Office || power.Lock == PowerLock.None);
            button.IsChecked = power?.Mode == mode;
            // The app sets each mode's CPU limit on Linux (see LinuxPowerHardware.SetMode).
            _modeWatts[mode].Text = $"işlemci {LinuxPowerHardware.ModePl1Watts(mode)} W";
        }
        _rendering = false;

        _cpuWatts.Text = power?.CpuWatts is { } cw ? $"{cw} W" : "–";
        _cpuBar.Percent = power is { CpuWatts: { } w1, CpuPl1Watts: > 0 } ? Math.Min(100, w1 * 100 / power.CpuPl1Watts.Value) : null;
        _cpuPl1.Text = power?.CpuPl1Watts is { } pl1 ? $"Uzun süreli sınır {pl1} W" : "İşlemci gücü okunamıyor";
        _cpuPl2.Text = power?.CpuPl2Watts is { } pl2 ? $"Kısa süreli sınır {pl2} W" : "";
        _cpuPl2Row.IsVisible = power?.CpuPl2Watts is not null;
        _gpuWatts.Text = power?.GpuPowerWatts is { } gw ? $"{gw} W" : "–";
        // With the user's own limit set, the bar and text follow it; the driver's limit stays above it.
        int? gpuCap = power?.GpuLimitWatts ?? power?.GpuWatts;
        _gpuBar.Percent = power?.GpuPowerWatts is { } w2 && gpuCap is > 0 ? Math.Min(100, w2 * 100 / gpuCap.Value) : null;
        _gpuLimit.Text = power?.GpuLimitWatts is { } own
            ? power.GpuWatts is { } drv ? $"Sınırınız {own} W (sürücünün sınırı {drv} W)" : $"Sınırınız {own} W"
            : power?.GpuWatts is { } gl ? $"Sınır {gl} W" : "Ekran kartı okunamıyor (uykuda ya da NVIDIA sürücüsü yok)";

        RenderLimit();
        RenderGpuLimit();

        if (power is null)
        {
            _now.Text = "Fan servisine bağlanılamıyor.";
            _note.Text = "";
            return;
        }
        string source = power.OnBattery ? $"pilde (%{power.BatteryPercent})" : power.UsbCAdapter ? "USB-C adaptörde" : "prizde";
        _now.Text = power.Mode is { } m ? $"Şu an: {Modes.First(x => x.Mode == m).Label} · {source}" : $"Şu anki mod okunamadı · {source}";
        _note.Text = power.Lock switch
        {
            PowerLock.OnBattery => "Pilde bilgisayar kendiliğinden Ofis moduna geçer. Prize takınca seçtiğiniz mod geri gelir.",
            PowerLock.UsbCAdapter => "Bu USB-C adaptör yeterince güçlü değil; yalnız Ofis modu açık. Prize takınca seçtiğiniz mod geri gelir.",
            _ => power.Preferred is { } p ? $"Prizdeyken seçtiğiniz mod: {Modes.First(x => x.Mode == p).Label}." : "",
        };
    }

    private void RenderLimit()
    {
        var power = _connection.Connected ? _connection.Status?.Power : null;
        _rendering = true;
        _byMode.IsEnabled = _own.IsEnabled = power is not null;
        // The service's choice shows unless the user is in the middle of changing it.
        if (!_limitDirty && power is not null)
        {
            _byMode.IsChecked = power.CpuLimit is null;
            _own.IsChecked = power.CpuLimit is not null;
            if (power.CpuLimit is { } limit) { _pl1.Value = limit.Pl1Watts; _pl2.Value = limit.Pl2Watts; }
        }
        _rendering = false;
        SliderTexts();
        _ownPanel.IsVisible = _own.IsChecked == true;

        // Linux: the user's limit replaces the mode's (the app sets both), so a value above the mode's is not "ineffective" here.
        _limitNote.Text = power?.CpuLimit is { } on && !_limitDirty ? $"Uygulanıyor: uzun süreli {on.Pl1Watts} W, kısa süreli {on.Pl2Watts} W."
            : _limitDirty ? "Değişiklikler \"Uygula\"ya basınca geçerli olur." : "";
    }

    private void RenderGpuLimit()
    {
        var power = _connection.Connected ? _connection.Status?.Power : null;
        _rendering = true;
        _gpuByMode.IsEnabled = _gpuOwn.IsEnabled = power is not null;
        // Up to what the driver allows now: a higher target would do nothing.
        if (power?.GpuWatts is { } enforced)
            _gpuSlider.Maximum = Math.Clamp(enforced / GpuLimit.Step * GpuLimit.Step, GpuLimit.MinWatts, GpuLimit.MaxWatts);
        if (!_gpuDirty && power is not null)
        {
            _gpuByMode.IsChecked = power.GpuLimitWatts is null;
            _gpuOwn.IsChecked = power.GpuLimitWatts is not null;
            if (power.GpuLimitWatts is { } w) _gpuSlider.Value = w;
        }
        _rendering = false;
        _gpuText.Text = $"{_gpuSlider.Value:0} W";
        _gpuPanel.IsVisible = _gpuOwn.IsChecked == true;

        _gpuNote.Text = _gpuDirty ? "Değişiklik \"Uygula\"ya basınca geçerli olur."
            : power?.GpuLimitWatts is { } on
                ? power.GpuLockMhz is { } mhz ? $"Uygulanıyor: en çok {on} W (ekran kartı şu an en çok {mhz} MHz'de)." : $"Uygulanıyor: en çok {on} W (şu an sınırın altında)."
            : "";
    }
}
