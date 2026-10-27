using System.Windows;
using System.Windows.Controls;
using Excalibur.App.Ui.Controls;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Power;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Performans: the power mode (which sets both the graphics card's and the CPU's limits), the power both
/// draw right now, and the user's own CPU and graphics card limits. The service decides what is allowed: on battery or a weak
/// USB-C adapter only Office, so the other two are greyed out then.
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
    private const string GpuTip = "Ekran kartının çekebileceği en yüksek güç. Güç modu belirler; her modun değeri mod düğmelerinde yazar.";
    private const string GpuLimitTip = "Ekran kartı bu gücün üstüne çıkmasın diye hızı birkaç saniyede bir ayarlanır. " +
        "Boştayken etkisi yoktur; ağır oyunlarda sınıra kadar çıkar.";
    private const string NowTip = "Şu an çekilen güç. Boştayken düşüktür, oyunlarda ve ağır işlerde sınıra yaklaşır.";

    private readonly FanConnection _connection;
    private readonly Dictionary<PowerMode, RadioButton> _buttons = [];
    // What each mode allows on this laptop, as the service measured it (another model's card differs from the G870's).
    private readonly Dictionary<PowerMode, TextBlock> _modeWatts = [];
    private readonly TextBlock _now, _note, _error;
    private readonly TextBlock _cpuWatts, _cpuPl1, _cpuPl2, _gpuWatts, _gpuLimit;
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
        _now = InfoPage.Text("", "Body");
        _note = InfoPage.Text("", "Caption", top: 8);
        _error = ErrorText();

        // ---- power mode ----
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        string group = "powermode" + GetHashCode();
        foreach (var (mode, label) in Modes)
        {
            var b = new RadioButton { GroupName = group, Margin = new Thickness(0, 0, 8, 8), MinWidth = 210, Height = 58 };
            b.Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = label, FontWeight = FontWeights.SemiBold },
                    (_modeWatts[mode] = InfoPage.Text("", "Caption", top: 2)),
                },
            };
            b.SetResourceReference(StyleProperty, "Segment");
            b.Checked += async (_, _) =>
            {
                if (_rendering) return;
                var r = await _connection.RequestAsync(new FanRequest("setPowerMode", PowerMode: mode));
                Show(_error, r is null ? "Fan servisine ulaşılamadı." : r.Ok ? null : Message(r.Code, r.Error));
                Render();
            };
            _buttons[mode] = b;
            buttons.Children.Add(b);
        }

        // ---- power now ----
        _cpuWatts = InfoPage.Text("–", "BigValue");
        _gpuWatts = InfoPage.Text("–", "BigValue");
        _cpuBar.SetResourceReference(BlockBar.FillProperty, "Value");
        _cpuBar.SetResourceReference(BlockBar.EmptyProperty, "Border");
        _gpuBar.SetResourceReference(BlockBar.FillProperty, "Value");
        _gpuBar.SetResourceReference(BlockBar.EmptyProperty, "Border");
        var (pl1Row, pl1) = Term(Pl1Tip);
        var (pl2Row, pl2) = Term(Pl2Tip);
        var (gpuRow, gpuLimit) = Term(GpuTip);
        (_cpuPl1, _cpuPl2, _gpuLimit) = (pl1, pl2, gpuLimit);
        var meters = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(28) }, new ColumnDefinition() } };
        var cpuColumn = new StackPanel
        {
            Children = { InfoPage.Labeled("İŞLEMCİ", "Kicker", NowTip), _cpuWatts, _cpuBar, pl1Row, pl2Row },
        };
        var gpuColumn = new StackPanel
        {
            Children = { InfoPage.Labeled("EKRAN KARTI", "Kicker", NowTip), _gpuWatts, _gpuBar, gpuRow },
        };
        Grid.SetColumn(gpuColumn, 2);
        meters.Children.Add(cpuColumn);
        meters.Children.Add(gpuColumn);

        // ---- own CPU limit ----
        string limitGroup = "cpulimit" + GetHashCode();
        _byMode = Choice(limitGroup, "Güç modu belirlesin", "önerilen");
        _own = Choice(limitGroup, "Kendim sınırlayacağım", "daha serin ve sessiz");
        _byMode.Checked += async (_, _) => { if (!_rendering) await SendLimit(null); };
        _own.Checked += (_, _) => { if (!_rendering) { _limitDirty = true; RenderLimit(); } };

        (_pl1, _pl1Text) = (LimitSlider(CpuLimit.MinPl1, CpuLimit.MaxPl1, 35), InfoPage.Text("", "Body"));
        (_pl2, _pl2Text) = (LimitSlider(CpuLimit.MinPl2, CpuLimit.MaxPl2, 65), InfoPage.Text("", "Body"));
        // The short-term limit can never be below the long-term one.
        _pl1.ValueChanged += (_, _) => { if (_pl2.Value < _pl1.Value) _pl2.Value = Math.Max(_pl1.Value, CpuLimit.MinPl2); SliderTexts(); };
        _pl2.ValueChanged += (_, _) => { if (_pl1.Value > _pl2.Value) _pl1.Value = _pl2.Value; SliderTexts(); };
        _limitNote = InfoPage.Text("", "Caption", top: 6);
        _limitError = ErrorText();
        var apply = new Button { Content = "Uygula", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 110 };
        apply.SetResourceReference(StyleProperty, "AccentButton");
        apply.Click += async (_, _) => await SendLimit(new CpuLimit((int)_pl1.Value, (int)_pl2.Value));
        _ownPanel = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children = { SliderRow("Uzun süreli sınır", Pl1Tip, _pl1, _pl1Text), SliderRow("Kısa süreli sınır", Pl2Tip, _pl2, _pl2Text), _limitNote, apply },
        };
        SliderTexts();

        // ---- own graphics card limit ----
        string gpuGroup = "gpulimit" + GetHashCode();
        _gpuByMode = Choice(gpuGroup, "Güç modu belirlesin", "önerilen");
        _gpuOwn = Choice(gpuGroup, "Kendim sınırlayacağım", "daha serin ve sessiz");
        _gpuByMode.Checked += async (_, _) => { if (!_rendering) await SendGpuLimit(null); };
        _gpuOwn.Checked += (_, _) => { if (!_rendering) { _gpuDirty = true; RenderGpuLimit(); } };
        _gpuSlider = new Slider
        {
            Minimum = GpuLimit.MinWatts, Maximum = 75, Value = 50, TickFrequency = GpuLimit.Step, IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0),
        };
        _gpuSlider.SetResourceReference(StyleProperty, "ThinSlider");
        _gpuSlider.ValueChanged += (_, _) => { if (!_rendering) _gpuDirty = true; _gpuText.Text = $"{_gpuSlider.Value:0} W"; };
        _gpuText = InfoPage.Text($"{_gpuSlider.Value:0} W", "Body");
        _gpuNote = InfoPage.Text("", "Caption", top: 6);
        _gpuError = ErrorText();
        var gpuApply = new Button { Content = "Uygula", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 110 };
        gpuApply.SetResourceReference(StyleProperty, "AccentButton");
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
                InfoPage.Text("Performans", "PageTitle"),
                InfoPage.Text("Dizüstünün güç ayarı ve işlemci ile ekran kartının şu an çektiği güç.", "Caption", top: 2, bottom: 18),
                InfoPage.Card("Güç modu",
                    InfoPage.Text("Ekran kartının ve işlemcinin çekebileceği gücü belirler. Daha yüksek mod daha hızlı ama daha sıcak ve daha gürültülüdür.", "Caption"),
                    buttons, _now, _note, _error),
                InfoPage.Card("Anlık güç", meters),
                SideBySide(
                    InfoPage.Card("İşlemci güç sınırı",
                        InfoPage.Text("İşlemcinin gücünü güç modunun izin verdiğinin altına indirebilirsiniz: daha az ısı ve fan sesi, ağır işlerde biraz daha yavaş çalışma. " +
                                      "Güç modunun sınırından yüksek bir değer etkisizdir.", "Caption"),
                        new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Children = { _byMode, _own } },
                        _ownPanel, _limitError),
                    InfoPage.Card("Ekran kartı güç sınırı",
                        InfoPage.Text("Ekran kartının gücünü güç modunun izin verdiğinin altına indirebilirsiniz: daha az ısı ve fan sesi, oyunlarda biraz daha düşük kare hızı. " +
                                      "USB-C şarj aletiyle oynarken de işe yarar.", "Caption"),
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
        var t = InfoPage.Text("", "Caption", top: 6);
        t.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        t.Visibility = Visibility.Collapsed;
        return t;
    }

    /// <summary>Two cards in one row, the same height.</summary>
    private static Grid SideBySide(UIElement left, UIElement right)
    {
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(14) }, new ColumnDefinition() } };
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>A caption line whose text changes, followed by its (i).</summary>
    private static (StackPanel Row, TextBlock Text) Term(string tip)
    {
        var row = InfoPage.Labeled("", "Caption", tip, top: 6);
        return (row, (TextBlock)row.Children[0]);
    }

    private RadioButton Choice(string group, string label, string detail)
    {
        var b = new RadioButton
        {
            GroupName = group, Margin = new Thickness(0, 0, 8, 8), MinWidth = 180, Height = 58,
            Content = new StackPanel { Children = { new TextBlock { Text = label, FontWeight = FontWeights.SemiBold }, InfoPage.Text(detail, "Caption", top: 2) } },
        };
        b.SetResourceReference(StyleProperty, "Segment");
        return b;
    }

    private Slider LimitSlider(int min, int max, int value)
    {
        var s = new Slider
        {
            Minimum = min, Maximum = max, Value = value, TickFrequency = CpuLimit.Step, IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0),
        };
        s.SetResourceReference(StyleProperty, "ThinSlider");
        s.ValueChanged += (_, _) => { if (!_rendering) _limitDirty = true; };
        return s;
    }

    private static Grid SliderRow(string label, string tip, Slider slider, TextBlock value)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 6, 0, 0),
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(170) }, new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(60) } },
        };
        var name = InfoPage.Labeled(label, "Body", tip);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        value.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        return grid;
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
        target.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- rendering -------------------------------------------------------------------------

    private void Render()
    {
        var power = _connection.Connected ? _connection.Status?.Power : null;
        _rendering = true;
        foreach (var (mode, button) in _buttons)
        {
            bool allowed = power is not null && (mode == PowerMode.Office || power.Lock == PowerLock.None);
            button.IsEnabled = allowed;
            button.IsChecked = power?.Mode == mode;
            var w = power?.Modes?.FirstOrDefault(x => x.Mode == mode);
            _modeWatts[mode].Text = w is { GpuWatts: { } g }
                ? w.CpuWatts is { } c ? $"işlemci {c} W · ekran kartı {g} W" : $"ekran kartı {g} W"
                : "seçilince ölçülür";
        }
        _rendering = false;

        _cpuWatts.Text = power?.CpuWatts is { } cw ? $"{cw} W" : "–";
        _cpuBar.Percent = power is { CpuWatts: { } w1, CpuPl1Watts: > 0 } ? Math.Min(100, w1 * 100 / power.CpuPl1Watts.Value) : null;
        _cpuPl1.Text = power?.CpuPl1Watts is { } pl1 ? $"Uzun süreli sınır {pl1} W" : "İşlemci gücü okunamıyor";
        _cpuPl2.Text = power?.CpuPl2Watts is { } pl2 ? $"Kısa süreli sınır {pl2} W" : "";
        _cpuPl2.Parent.SetValue(VisibilityProperty, power?.CpuPl2Watts is null ? Visibility.Collapsed : Visibility.Visible);
        _gpuWatts.Text = power?.GpuPowerWatts is { } gw ? $"{gw} W" : "–";
        _gpuBar.Percent = power is { GpuPowerWatts: { } w2, GpuWatts: > 0 } ? Math.Min(100, w2 * 100 / power.GpuWatts.Value) : null;
        _gpuLimit.Text = power?.GpuWatts is { } gl ? $"Sınır {gl} W" : "Ekran kartı okunamıyor";

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
        _ownPanel.Visibility = _own.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        int? modeLimit = power?.Mode is { } m ? power.Modes?.FirstOrDefault(x => x.Mode == m)?.CpuWatts : null;
        _limitNote.Text = modeLimit is { } ml && _pl1.Value > ml
            ? $"Şu anki güç modunda uzun süreli sınır en çok {ml} W; daha yüksek değer etkisiz kalır."
            : power?.CpuLimit is { } on && !_limitDirty ? $"Uygulanıyor: uzun süreli {on.Pl1Watts} W, kısa süreli {on.Pl2Watts} W."
            : _limitDirty ? "Değişiklikler \"Uygula\"ya basınca geçerli olur." : "";
    }

    private void RenderGpuLimit()
    {
        var power = _connection.Connected ? _connection.Status?.Power : null;
        _rendering = true;
        _gpuByMode.IsEnabled = _gpuOwn.IsEnabled = power is not null;
        // Up to what the driver allows now (the power mode's limit, read live): a higher target would do nothing.
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
        _gpuPanel.Visibility = _gpuOwn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        _gpuNote.Text = _gpuDirty ? "Değişiklik \"Uygula\"ya basınca geçerli olur."
            : power?.GpuLimitWatts is { } on
                ? power.GpuLockMhz is { } mhz ? $"Uygulanıyor: en çok {on} W (ekran kartı şu an en çok {mhz} MHz'de)." : $"Uygulanıyor: en çok {on} W (şu an sınırın altında)."
            : "";
    }
}
