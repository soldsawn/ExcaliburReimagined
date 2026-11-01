using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Excalibur.App;
using Excalibur.App.Tray;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Power;
using Excalibur.Linux.Ui.Controls;

namespace Excalibur.Linux.Ui.Pages;

/// <summary>The landing page, as on Windows (OverviewPage.xaml): both chips at a glance, power, the fan profile and power mode, the system facts.</summary>
internal sealed class OverviewPage : UserControl
{
    private readonly FanConnection _connection;
    private readonly Dictionary<string, TextBlock> _facts = [];
    private readonly TextBlock _subtitle, _profileHint, _profileError, _powerHint, _powerError, _trialTitle, _trialText;
    private readonly Border _faultBanner, _trialBanner;
    private readonly Button _startTrial, _closeTrial, _copyInfo;
    private readonly (TextBlock Temp, TextBlock Fan, TextBlock Duty, BlockBar Bar) _cpu, _gpu;
    private readonly WrapPanel _powerButtonsPanel = new();
    private bool _trialClosed;
    private string? _trialError;

    internal OverviewPage(FanConnection connection)
    {
        _connection = connection;
        var root = new StackPanel();
        root.Children.Add(Kit.Text("Panel", "PageTitle"));
        _subtitle = Kit.Text("", "Caption", top: 2, bottom: 18);
        _subtitle.FontSize = 13;
        root.Children.Add(_subtitle);

        // fault banner
        var clearFault = Kit.Button("Tekrar dene", "DangerButton");
        clearFault.Click += async (_, _) => await _connection.SendAsync(new FanRequest("clearFault"));
        var faultTitle = Kit.Text("Fanlarda bir sorun oldu", "CardTitle").Res(TextBlock.ForegroundProperty, "Bad");
        _faultBanner = Banner("Bad", clearFault, faultTitle,
            Kit.Text("Uygulama fan bilgilerini güvenle okuyamadı, bu yüzden fanları orijinal ayara bıraktı. \"Tekrar dene\" ile yeniden başlatabilirsiniz.", "Caption", top: 4));
        root.Children.Add(_faultBanner);

        // unknown fan firmware: monitor only, the user may try it
        _copyInfo = Kit.Button("Bilgileri kopyala");
        _startTrial = Kit.Button("Dene", "AccentButton");
        _startTrial.Margin = new Thickness(8, 0, 0, 0);
        _closeTrial = Kit.Button("Kapat");
        _closeTrial.Margin = new Thickness(8, 0, 0, 0);
        _trialTitle = Kit.Text("", "CardTitle");
        _trialText = Kit.Text("", "Caption", top: 4);
        _trialBanner = Banner("Warn", new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _copyInfo, _startTrial, _closeTrial } },
            _trialTitle, _trialText);
        root.Children.Add(_trialBanner);
        _startTrial.Click += async (_, _) => await StartTrialAsync();
        _closeTrial.Click += (_, _) => { _trialClosed = true; Render(); };
        _copyInfo.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(SupportLine(_connection.Status));
                _copyInfo.Content = "Kopyalandı";
            }
        };

        // CPU / GPU
        var chips = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,*") };
        chips.Children.Add(ChipCard("İŞLEMCİ", HardwareNames.Cpu, out _cpu));
        var gpuCard = ChipCard("EKRAN KARTI", HardwareNames.Gpu, out _gpu);
        Grid.SetColumn(gpuCard, 2);
        chips.Children.Add(gpuCard);
        root.Children.Add(chips);

        // power at a glance
        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var key in new[] { "mode", "source", "hottest" }) stats.Children.Add(StatBox(key));
        root.Children.Add(stats);

        // fan profile and power mode side by side: the two choices made most often
        var selector = new ProfileSelector(connection);
        _profileHint = Kit.Text("", "Caption", top: 4, bottom: 12);
        _profileError = Kit.Text("", "Caption").Res(TextBlock.ForegroundProperty, "Bad");
        _profileError.IsVisible = false;
        selector.CommandFailed += error => { _profileError.Text = error; _profileError.IsVisible = true; };
        var profileCard = new BracketCard
        {
            Child = new StackPanel { Children = { Kit.Text("Fan profili", "CardTitle"), _profileHint, selector, _profileError } },
        }.Classed("CardBox");
        _powerHint = Kit.Text("", "Caption", top: 4, bottom: 12);
        _powerError = Kit.Text("", "Caption", top: 6).Res(TextBlock.ForegroundProperty, "Bad");
        _powerError.IsVisible = false;
        var powerCard = new BracketCard
        {
            Child = new StackPanel { Children = { Kit.Text("Güç modu", "CardTitle"), _powerHint, _powerButtonsPanel, _powerError } },
        }.Classed("CardBox");
        Grid.SetColumn(powerCard, 2);
        root.Children.Add(new Grid
        {
            Margin = new Thickness(0, 14, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("*,14,Auto"),
            Children = { profileCard, powerCard },
        });

        // facts
        var facts = new UniformGrid { Columns = 4 };
        foreach (string label in new[] { "Fan kontrolü", "Fanları yöneten", "Fn+1 turbo", "Açık kalma süresi" })
        {
            var value = Kit.Text("", "Body", top: 2);
            facts.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 12, 4), Children = { Kit.Text(label, "Caption"), value } });
            _facts[label] = value;
        }
        root.Children.Add(new BracketCard
        {
            Margin = new Thickness(0, 14, 0, 0),
            Child = new StackPanel { Children = { Kit.Text("Sistem", "CardTitle", bottom: 12), facts } },
        }.Classed("CardBox"));

        Content = root;
        BuildPowerButtons();
        _connection.Changed += Render;
        Render();
    }

    private static Border Banner(string brush, Control right, params Control[] left)
    {
        var dock = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        var stack = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        foreach (var c in left) stack.Children.Add(c);
        dock.Children.Add(stack);
        var banner = new Border { Child = dock, Margin = new Thickness(0, 0, 0, 14), IsVisible = false }.Classed("CardBox");
        banner.Res(Border.BorderBrushProperty, brush);
        return banner;
    }

    private BracketCard ChipCard(string title, string? name, out (TextBlock, TextBlock, TextBlock, BlockBar) parts)
    {
        var header = new DockPanel();
        var nameText = Kit.Text(name ?? "", "Caption");
        nameText.HorizontalAlignment = HorizontalAlignment.Right;
        nameText.TextWrapping = TextWrapping.NoWrap;
        var titleText = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold }.Res(TextBlock.ForegroundProperty, "SubText");
        header.Children.Add(titleText);
        header.Children.Add(nameText);
        var temp = Kit.Text("–", "BigValue", top: 6);
        var fan = Kit.Text("–", "Mono");
        var duty = Kit.Text("", "Mono");
        duty.HorizontalAlignment = HorizontalAlignment.Right;
        var fanRow = new DockPanel { Margin = new Thickness(0, 4, 0, 10), Children = { fan, duty } };
        var bar = new BlockBar { Height = 8 }.Res(BlockBar.FillProperty, "Value").Res(BlockBar.EmptyProperty, "CardHover");
        parts = (temp, fan, duty, bar);
        return new BracketCard
        {
            Padding = new Thickness(20, 16, 20, 18),
            Child = new StackPanel { Children = { header, temp, fanRow, bar } },
        }.Classed("CardBox");
    }

    private void Render()
    {
        var s = _connection.Status;
        bool on = _connection.Connected && s is not null;
        _subtitle.Text = on ? $"{ProfileNames.Label(s!.Profile, s.ManualUntil)} profili açık · {Texts.Driver(s.HoldsFans)}"
                            : _connection.Problem is { } problem ? $"Fan kontrolü kapalı: {problem}" : "Fan kontrolüne bağlanılamıyor.";
        Chip(on ? s!.CpuTempC : null, on ? s!.CpuRpm : null, on ? s!.CpuTarget : null, on ? s!.CpuAppliedPercent : null, _cpu);
        Chip(on ? s!.GpuTempC : null, on ? s!.GpuRpm : null, on ? s!.GpuTarget : null, on ? s!.GpuAppliedPercent : null, _gpu);

        RenderStats(on ? s : null);
        RenderPower(on ? s!.Power : null);

        _faultBanner.IsVisible = on && s!.Mode == FanLoopMode.Faulted;
        RenderTrial(on ? s : null);
        _profileHint.Text = on ? Hint(s!) : "";
        if (on) _profileError.IsVisible = false;

        _facts["Fan kontrolü"].Text = !on ? "Bağlantı yok" : s!.Mode switch
        {
            FanLoopMode.Running => "Açık",
            FanLoopMode.MonitorOnly => "Yalnız izleniyor",
            FanLoopMode.Faulted => "Sorun var",
            _ => "Durdu",
        } + (s.Simulated ? " (deneme)" : "");
        _facts["Fanları yöneten"].Text = !on ? "–" : s!.HoldsFans ? "Excalibur Reimagined" : "Orijinal ayar";
        _facts["Fn+1 turbo"].Text = !on ? "–" : s!.CoolingBoost ? "Açık (iki fan tam hızda)" : "Kapalı";
        _facts["Açık kalma süresi"].Text = !on ? "–" : Texts.Duration(DateTimeOffset.Now - s!.ServiceStartedAt);
    }

    /// <summary>The unknown-fan-firmware card: what is known, the trial's progress or result, and the line to send the developer.</summary>
    private void RenderTrial(FanStatusDto? s)
    {
        var t = s?.Trial;
        bool unknown = s?.Mode == FanLoopMode.MonitorOnly;
        bool show = s is not null && (unknown || (t?.State == EcTrialState.Passed && !_trialClosed));
        _trialBanner.IsVisible = show;
        if (!show) return;

        string firmware = s!.EcVersion is { } v ? $"Fan yazılımı sürümü: {v}. " : "Fan yazılımı sürümü okunamadı. ";
        (_trialTitle.Text, _trialText.Text) = t?.State switch
        {
            EcTrialState.Running => ("Fan kontrolü deneniyor",
                $"{t.Fan switch { FanId.Gpu => "Ekran kartı fanı", FanId.Cpu => "İşlemci fanı", _ => "İki fan" }} birkaç saniye farklı hızda dönecek. " +
                "Deneme bitince fanlar orijinal ayara döner."),
            EcTrialState.Passed => ("Deneme başarılı",
                "İki fan da uygulamanın komutlarına uydu; fan kontrolü bu dizüstünde açıldı. " +
                (s.Profile == FanProfile.EcAuto.Name
                    ? "Hazır profiller başka bir modele göre ayarlandığı için fanlar şimdilik orijinal ayarda kalıyor; Soğutma sayfasından bir profil " +
                      "seçebilir ya da kendi eğrinizi kurabilirsiniz. "
                    : "") +
                "\"Bilgileri kopyala\" ile sonucu geliştiriciye gönderebilirsiniz, böylece bu dizüstü sonraki sürümlerde listeye eklenir."),
            EcTrialState.Failed => ("Deneme başarısız", TrialProblem(t) + " Fanlar orijinal ayarda; uygulama fanları yalnız izlemeye devam ediyor."),
            _ when s.EcSeenElsewhere => ("Fan kontrolü için kısa bir ayar gerekiyor", firmware +
                "Bu fan yazılımı başka bir Excalibur'da sorunsuz çalıştı. Bu dizüstüne uyması için kısa bir ayar yapılacak (yaklaşık 10 sn): " +
                "fanlar birkaç saniye farklı hızda döner, sonra orijinal ayara geri döner."),
            _ => ("Bu dizüstünün fan yazılımı tanınmadı", firmware +
                "Bu sürüm henüz denenmediği için uygulama fanları yalnız izliyor. Kısa bir denemeyle fan kontrolünün burada çalışıp çalışmadığına bakılabilir."),
        };
        if (_trialError is not null) _trialText.Text += $" ({_trialError})";
        _startTrial.IsVisible = unknown && s.EcVersion is not null && t?.State != EcTrialState.Running;
        _startTrial.Content = t?.State == EcTrialState.Failed ? "Tekrar dene" : s.EcSeenElsewhere ? "Başlat" : "Dene";
        _closeTrial.IsVisible = t?.State == EcTrialState.Passed;
    }

    private async Task StartTrialAsync()
    {
        string firmware = _connection.Status?.EcVersion ?? "?";
        // A version that already passed elsewhere: the card says what happens and its button is the consent.
        bool start = _connection.Status?.EcSeenElsewhere == true || await ConfirmDialog.Ask(this, "Fan kontrolünü dene",
            $"Bu fan yazılımı sürümü ({firmware}) henüz denenmedi.\n\n" +
            "Deneme en çok bir dakika sürer: fanlar tek tek birkaç saniye farklı hızda döner ve hızın gerçekten değişip değişmediğine bakılır. " +
            "Her denemeden sonra fanlar orijinal ayara döner. Dizüstü 75 °C'nin altındayken başlar, 80 °C'yi geçerse durur.\n\n" +
            "Denemeyi başlatmak istiyor musunuz?",
            "Denemeyi başlat");
        if (!start) return;
        _trialError = await _connection.SendAsync(new FanRequest("trialEc"));
        _copyInfo.Content = "Bilgileri kopyala";
        Render();
    }

    private static string TrialProblem(EcTrialStatus t) => t.Problem switch
    {
        EcTrialProblem.TooHot => "Dizüstü fazla sıcak: deneme 75 °C'nin altında başlar ve 80 °C'yi geçince durur. Biraz bekleyip tekrar deneyin.",
        EcTrialProblem.NoFollow => $"{(t.CpuFollows == false ? "İşlemci" : "Ekran kartı")} fanının hızı değişmedi; bu sürümde fan ayarları başka bir yerde olabilir.",
        EcTrialProblem.WriteFailed => "Fan ayarı yazılamadı.",
        EcTrialProblem.ReadFailed => "Fan bilgileri okunamadı.",
        EcTrialProblem.Stopped => "Deneme yarıda kaldı (hizmet durdu ya da bilgisayar uyudu).",
        _ => "Bilinmeyen bir sorun oldu.",
    };

    /// <summary>One line for the developer: app version, model, BIOS, fan firmware and the trial's result.</summary>
    internal static string SupportLine(FanStatusDto? s)
    {
        string app = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        var t = s?.Trial;
        string Fan(bool? follows) => follows switch { true => "uydu", false => "uymadı", null => "denenmedi" };
        string result = t?.State switch
        {
            EcTrialState.Running => "sürüyor",
            EcTrialState.Passed => "başarılı",
            EcTrialState.Failed => $"başarısız ({t.Problem})",
            _ => "yapılmadı",
        };
        return $"Excalibur Reimagined {app} (Linux) · Model: {HardwareNames.Model ?? "?"} · BIOS: {HardwareNames.Bios ?? "?"} · " +
               $"Fan yazılımı (EC): {s?.EcVersion ?? "?"} · Deneme: {result}" +
               (t is null ? "" : $" · işlemci fanı {Fan(t.CpuFollows)}, ekran kartı fanı {Fan(t.GpuFollows)}");
    }

    // The power mode next to the fan profile, so both are one click away (the Performans page has the same choice, with more room).
    private static readonly (PowerMode Mode, string Label, string Full)[] PowerModes =
    [
        (PowerMode.Office, "Ofis", "Ofis modu"), (PowerMode.Gaming, "Oyun", "Oyun modu"), (PowerMode.HighPerformance, "Yüksek", "Yüksek performans"),
    ];
    private readonly Dictionary<PowerMode, RadioButton> _powerButtons = [];
    private readonly Dictionary<PowerMode, TextBlock> _powerWatts = [];
    private bool _renderingPower;

    private void BuildPowerButtons()
    {
        string group = "overviewpower" + GetHashCode();
        foreach (var (mode, label, full) in PowerModes)
        {
            var watts = Kit.Text("–", "Caption", top: 2);
            watts.HorizontalAlignment = HorizontalAlignment.Center;
            var b = Kit.Segment(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    watts,
                },
            }, group);
            b.MinWidth = 0;
            b.Height = 52;
            ToolTip.SetTip(b, full);
            _powerWatts[mode] = watts;
            b.IsCheckedChanged += async (_, _) =>
            {
                if (_renderingPower || b.IsChecked != true) return;
                var r = await _connection.RequestAsync(new FanRequest("setPowerMode", PowerMode: mode));
                _powerError.Text = r is null ? "Fan servisine ulaşılamadı." : r.Ok ? "" : r.Code switch
                {
                    "power_battery" => "Pilde yalnızca Ofis modu kullanılabilir.",
                    "power_usbc" => "Bu USB-C adaptörde yalnızca Ofis modu kullanılabilir.",
                    _ => "Güç modu değiştirilemedi.",
                };
                _powerError.IsVisible = _powerError.Text.Length > 0;
                Render();
            };
            _powerButtons[mode] = b;
            _powerButtonsPanel.Children.Add(b);
        }
    }

    private void RenderPower(PowerStatusDto? power)
    {
        _renderingPower = true;
        foreach (var (mode, button) in _powerButtons)
        {
            button.IsEnabled = power is not null && (mode == PowerMode.Office || power.Lock == PowerLock.None);
            button.IsChecked = power?.Mode == mode;
            // What the mode allows on this laptop, as the service measured it; nothing until it has.
            var w = power?.Modes?.FirstOrDefault(x => x.Mode == mode);
            string full = PowerModes.First(x => x.Mode == mode).Full;
            // Linux: the mode moves only the CPU's limit (the graphics card's needs the BIOS's ACPI call).
            _powerWatts[mode].Text = w?.CpuWatts is { } c ? $"{c} W" : "–";
            ToolTip.SetTip(button, w?.CpuWatts is { } cw ? $"{full}: işlemci en çok {cw} W" : $"{full}: seçilince ölçülür");
        }
        _renderingPower = false;
        _powerHint.Text = power?.Lock switch
        {
            null => "",
            PowerLock.OnBattery => "Pilde yalnızca Ofis modu çalışır.",
            PowerLock.UsbCAdapter => "USB-C adaptörde yalnızca Ofis modu.",
            _ => "İşlemci güç sınırı",
        };
    }

    private readonly Dictionary<string, (TextBlock Value, TextBlock Caption)> _stats = [];

    /// <summary>A kicker, a big number-font value and a one-line caption in a bracketed card.</summary>
    private BracketCard StatBox(string key)
    {
        var kicker = Kit.Text(key switch { "mode" => "GÜÇ MODU", "source" => "GÜÇ KAYNAĞI", _ => "EN SICAK" }, "Kicker");
        var value = Kit.Text("", "BigValue", top: 4);
        value.FontSize = 30;
        var caption = Kit.Text("", "Caption");
        _stats[key] = (value, caption);
        return new BracketCard
        {
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(key == "mode" ? 0 : 7, 0, key == "hottest" ? 0 : 7, 0),
            Child = new StackPanel { Children = { kicker, value, caption } },
        }.Classed("CardBox");
    }

    private void RenderStats(FanStatusDto? s)
    {
        var power = s?.Power;
        Set("mode", power?.Mode is { } m ? Texts.PowerMode(m) : "–",
            string.Join(" · ", new[]
            {
                power?.CpuWatts is { } cw ? $"işlemci {cw}" + (power.CpuPl1Watts is { } l ? $" / {l} W" : " W") : null,
                // The user's own GPU limit, when set, is the one that counts (the driver's stays higher).
                power?.GpuPowerWatts is { } gw ? $"ekran kartı {gw}" + ((power.GpuLimitWatts ?? power.GpuWatts) is { } gl ? $" / {gl} W" : " W")
                    : (power?.GpuLimitWatts ?? power?.GpuWatts) is { } w ? $"ekran kartı sınırı {w} W" : null,
            }.Where(x => x is not null)));
        Set("source", s is null || power is null ? "–" : power.OnBattery ? "Pil" : power.UsbCAdapter ? "USB-C" : "Priz",
            power is null ? "" : power.OnBattery ? $"%{power.BatteryPercent} · yalnız Ofis modu" : power.BatteryPercent is { } b ? $"pil %{b}" : "");
        int? hottest = s is null ? null : s.CpuTempC is { } c && s.GpuTempC is { } g ? Math.Max(c, g) : s.CpuTempC ?? s.GpuTempC;
        Set("hottest", hottest is { } h ? $"{h}°C" : "–",
            s is null ? "" : s.CpuTempC >= (s.GpuTempC ?? 0) ? "işlemci" : "ekran kartı");
        _stats["hottest"].Value.Foreground = Palette.Temperature(this, hottest);

        void Set(string key, string value, string caption)
        {
            _stats[key].Value.Text = value;
            _stats[key].Caption.Text = caption;
        }
    }

    private void Chip(int? temp, int? rpm, int? target, int? applied, (TextBlock Temp, TextBlock Fan, TextBlock Duty, BlockBar Bar) chip)
    {
        chip.Temp.Text = temp is { } t ? $"{t}°C" : "–";
        chip.Temp.Foreground = Palette.Temperature(this, temp);
        chip.Fan.Text = rpm is { } r ? $"{r} rpm" : "–";
        // Under the EC's own control there is no target, but the speed worked out from the RPM still fills the bar.
        int? percent = applied ?? target;
        chip.Duty.Text = rpm is null ? "" : (percent is { } p ? $"%{p}" : "") + (target is null ? (percent is null ? "orijinal" : " · orijinal") : "");
        chip.Bar.Percent = percent;
        chip.Bar.Fill = Palette.Temperature(this, temp);
    }

    private static string Hint(FanStatusDto s) => s.Profile switch
    {
        "Quiet" => "Sessiz: fanlar yavaş döner, 62 °C'yi geçince hızlanır. Ofis işi ve film için.",
        "Balanced" => "Dengeli: orijinal ayardan daha sessiz, yük altında aynı serinlikte. Günlük kullanım için önerilir.",
        "Performance" => "Performans: fanlar erken ve hızlı döner. Oyun ve uzun süren işler için.",
        "EcAuto" => "Orijinal ayar: fanları dizüstünün kendisi yönetir, uygulama karışmaz.",
        "Manual" => $"Manuel: sabit hız; {s.ManualUntil:HH:mm}'de önceki profile döner.",
        _ => "Kendi oluşturduğunuz profil: fanlar sizin çizdiğiniz eğriyi izler.",
    };
}
