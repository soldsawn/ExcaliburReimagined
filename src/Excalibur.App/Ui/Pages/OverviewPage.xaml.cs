using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Excalibur.App.Tray;
using Excalibur.App.Ui.Controls;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Power;

namespace Excalibur.App.Ui.Pages;

/// <summary>The landing page: both chips at a glance, the fan profile, and the system facts.</summary>
public partial class OverviewPage : UserControl
{
    private readonly FanConnection _connection;
    private readonly Dictionary<string, TextBlock> _facts = [];
    private bool _trialClosed;
    private string? _trialError;

    internal OverviewPage(FanConnection connection)
    {
        _connection = connection;
        InitializeComponent();

        CpuName.Text = HardwareNames.Cpu ?? "";
        GpuName.Text = HardwareNames.Gpu ?? "";
        var selector = new ProfileSelector(connection);
        selector.CommandFailed += error => { ProfileError.Text = error; ProfileError.Visibility = Visibility.Visible; };
        ProfileHost.Content = selector;
        ClearFault.Click += async (_, _) => await _connection.SendAsync(new FanRequest("clearFault"));
        StartTrial.Click += async (_, _) => await StartTrialAsync();
        CloseTrial.Click += (_, _) => { _trialClosed = true; Render(); };
        CopyInfo.Click += (_, _) =>
        {
            try { Clipboard.SetText(SupportLine(_connection.Status)); CopyInfo.Content = "Kopyalandı"; }
            catch (System.Runtime.InteropServices.ExternalException) { } // the clipboard is busy; the user can click again
        };

        foreach (string label in new[] { "Fan kontrolü", "Fanları yöneten", "Fn+1 turbo", "Açık kalma süresi" })
        {
            var value = new TextBlock { Style = (Style)FindResource("Body"), Margin = new Thickness(0, 2, 0, 0) };
            Facts.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 0, 12, 4),
                Children = { new TextBlock { Text = label, Style = (Style)FindResource("Caption") }, value },
            });
            _facts[label] = value;
        }

        foreach (var key in new[] { "mode", "source", "hottest" })
            Stats.Children.Add(StatBox(key));
        BuildPowerButtons();
        _connection.Changed += Render;
        Render();
    }

    private void Render()
    {
        var s = _connection.Status;
        bool on = _connection.Connected && s is not null;
        Subtitle.Text = on ? $"{ProfileNames.Label(s!.Profile, s.ManualUntil)} profili açık · {Texts.Driver(s.HoldsFans)}"
                           : "Fan kontrolüne bağlanılamıyor.";
        Chip(on ? s!.CpuTempC : null, on ? s!.CpuRpm : null, on ? s!.CpuTarget : null, on ? s!.CpuAppliedPercent : null, CpuTemp, CpuFan, CpuDuty, CpuBar);
        Chip(on ? s!.GpuTempC : null, on ? s!.GpuRpm : null, on ? s!.GpuTarget : null, on ? s!.GpuAppliedPercent : null, GpuTemp, GpuFan, GpuDuty, GpuBar);

        RenderStats(on ? s : null);
        RenderPower(on ? s!.Power : null);

        FaultBanner.Visibility = on && s!.Mode == FanLoopMode.Faulted ? Visibility.Visible : Visibility.Collapsed;
        RenderTrial(on ? s : null);
        ProfileHint.Text = on ? Hint(s!) : "";
        if (on) ProfileError.Visibility = Visibility.Collapsed;

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
        TrialBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        string firmware = s!.EcVersion is { } v ? $"Fan yazılımı sürümü: {v}. " : "Fan yazılımı sürümü okunamadı. ";
        (TrialTitle.Text, TrialText.Text) = t?.State switch
        {
            EcTrialState.Running => ("Fan kontrolü deneniyor",
                $"{t.Fan switch { FanId.Gpu => "Ekran kartı fanı", FanId.Cpu => "İşlemci fanı", _ => "İki fan" }} birkaç saniye farklı hızda dönecek. " +
                "Deneme bitince fanlar orijinal ayara döner."),
            EcTrialState.Passed => ("Deneme başarılı",
                "İki fan da uygulamanın komutlarına uydu; fan kontrolü bu dizüstünde açıldı" +
                (t.Path == FanWritePath.Wmi ? " (BIOS üzerinden). " : ". ") +
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
        if (_trialError is not null) TrialText.Text += $" ({_trialError})";
        StartTrial.Visibility = unknown && s.EcVersion is not null && t?.State != EcTrialState.Running ? Visibility.Visible : Visibility.Collapsed;
        StartTrial.Content = t?.State == EcTrialState.Failed ? "Tekrar dene" : s.EcSeenElsewhere ? "Başlat" : "Dene";
        CloseTrial.Visibility = t?.State == EcTrialState.Passed ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task StartTrialAsync()
    {
        string firmware = _connection.Status?.EcVersion ?? "?";
        // A version that already passed elsewhere: the card says what happens and its button is the consent.
        bool start = _connection.Status?.EcSeenElsewhere == true || ConfirmDialog.Ask(this, "Fan kontrolünü dene",
            $"Bu fan yazılımı sürümü ({firmware}) henüz denenmedi.\n\n" +
            "Deneme en çok bir dakika sürer: fanlar önce birlikte BIOS üzerinden, olmazsa tek tek doğrudan birkaç saniye farklı hızda döner " +
            "ve hızın gerçekten değişip değişmediğine bakılır. Her denemeden sonra fanlar orijinal ayara döner. Dizüstü 75 °C'nin altındayken başlar, " +
            "80 °C'yi geçerse durur.\n\nDenemeyi başlatmak istiyor musunuz?",
            "Denemeyi başlat");
        if (!start) return;
        _trialError = await _connection.SendAsync(new FanRequest("trialEc"));
        CopyInfo.Content = "Bilgileri kopyala";
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
    private static string SupportLine(FanStatusDto? s)
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
        return $"Excalibur Reimagined {app} · Model: {HardwareNames.Model ?? "?"} · BIOS: {HardwareNames.Bios ?? "?"} · " +
               $"Fan yazılımı (EC): {s?.EcVersion ?? "?"} · Deneme: {result}" +
               (t is null ? "" : $" · WMI 0x0205: {Fan(t.WmiFollows)}, işlemci fanı {Fan(t.CpuFollows)}, ekran kartı fanı {Fan(t.GpuFollows)}") +
               (t is { State: EcTrialState.Passed } ? $" · yol: {(t.Path == FanWritePath.Wmi ? "WMI" : "EC")}" : "");
    }

    // The power mode next to the fan profile, so both are one click away (the Performans page has the same choice, with more room).
    // Short names keep both cards on one row at the window's default width; the full name is in the tooltip.
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
            var b = new RadioButton
            {
                GroupName = group, MinWidth = 0, Height = 52,
                ToolTip = full,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                        (_powerWatts[mode] = new TextBlock
                        {
                            Text = "–", Style = (Style)FindResource("Caption"),
                            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0),
                        }),
                    },
                },
            };
            b.SetResourceReference(StyleProperty, "Segment");
            b.Checked += async (_, _) =>
            {
                if (_renderingPower) return;
                var r = await _connection.RequestAsync(new FanRequest("setPowerMode", PowerMode: mode));
                PowerError.Text = r is null ? "Fan servisine ulaşılamadı." : r.Ok ? "" : r.Code switch
                {
                    "power_battery" => "Pilde yalnızca Ofis modu kullanılabilir.",
                    "power_usbc" => "Bu USB-C adaptörde yalnızca Ofis modu kullanılabilir.",
                    _ => "Güç modu değiştirilemedi.",
                };
                PowerError.Visibility = PowerError.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                Render();
            };
            _powerButtons[mode] = b;
            PowerButtons.Children.Add(b);
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
            _powerWatts[mode].Text = w is { GpuWatts: { } g } ? $"{w.CpuWatts?.ToString() ?? "–"}/{g} W" : "–";
            button.ToolTip = w is { GpuWatts: { } gw }
                ? $"{full}: {(w.CpuWatts is { } cw ? $"işlemci en çok {cw} W, " : "")}ekran kartı en çok {gw} W"
                : $"{full}: seçilince ölçülür";
        }
        _renderingPower = false;
        PowerHint.Text = power?.Lock switch
        {
            null => "",
            PowerLock.OnBattery => "Pilde yalnızca Ofis modu çalışır.",
            PowerLock.UsbCAdapter => "USB-C adaptörde yalnızca Ofis modu.",
            _ => "İşlemci / ekran kartı güç sınırı",
        };
    }

    private readonly Dictionary<string, (TextBlock Value, TextBlock Caption)> _stats = [];

    /// <summary>A kicker, a big number-font value and a one-line caption in a bracketed card (SawnTask / Armoury style).</summary>
    private BracketCard StatBox(string key)
    {
        var kicker = new TextBlock { Style = (Style)FindResource("Kicker"), Text = key switch { "mode" => "GÜÇ MODU", "source" => "GÜÇ KAYNAĞI", _ => "EN SICAK" } };
        var value = new TextBlock { Style = (Style)FindResource("BigValue"), FontSize = 30, Margin = new Thickness(0, 4, 0, 0) };
        var caption = new TextBlock { Style = (Style)FindResource("Caption") };
        _stats[key] = (value, caption);
        return new BracketCard
        {
            Style = (Style)FindResource("CardBox"),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(key == "mode" ? 0 : 7, 0, key == "hottest" ? 0 : 7, 0),
            Child = new StackPanel { Children = { kicker, value, caption } },
        };
    }

    private void RenderStats(FanStatusDto? s)
    {
        var power = s?.Power;
        Set("mode", power?.Mode is { } m ? Texts.PowerMode(m) : "–",
            string.Join(" · ", new[]
            {
                power?.CpuWatts is { } cw ? $"işlemci {cw}" + (power.CpuPl1Watts is { } l ? $" / {l} W" : " W") : null,
                power?.GpuPowerWatts is { } gw ? $"ekran kartı {gw}" + (power.GpuWatts is { } gl ? $" / {gl} W" : " W")
                    : power?.GpuWatts is { } w ? $"ekran kartı sınırı {w} W" : null,
            }.Where(x => x is not null)));
        Set("source", s is null || power is null ? "–" : power.OnBattery ? "Pil" : power.UsbCAdapter ? "USB-C" : "Priz",
            power is null ? "" : power.OnBattery ? $"%{power.BatteryPercent} · yalnız Ofis modu" : power.BatteryPercent is { } b ? $"pil %{b}" : "");
        int? hottest = s is null ? null : s.CpuTempC is { } c && s.GpuTempC is { } g ? Math.Max(c, g) : s.CpuTempC ?? s.GpuTempC;
        Set("hottest", hottest is { } h ? $"{h}°C" : "–",
            s is null ? "" : s.CpuTempC >= (s.GpuTempC ?? 0) ? "işlemci" : "ekran kartı");
        if (hottest is { } t) _stats["hottest"].Value.Foreground = Palette.Temperature(this, t);

        void Set(string key, string value, string caption)
        {
            _stats[key].Value.Text = value;
            _stats[key].Caption.Text = caption;
        }
    }

    private void Chip(int? temp, int? rpm, int? target, int? applied, TextBlock tempText, TextBlock fan, TextBlock duty, BlockBar bar)
    {
        tempText.Text = temp is { } t ? $"{t}°C" : "–";
        tempText.Foreground = Palette.Temperature(this, temp);
        fan.Text = rpm is { } r ? $"{r} rpm" : "–";
        // Under the EC's own control there is no target, but the speed worked out from the RPM still fills the bar.
        int? percent = applied ?? target;
        duty.Text = rpm is null ? "" : (percent is { } p ? $"%{p}" : "") + (target is null ? (percent is null ? "orijinal" : " · orijinal") : "");
        bar.Percent = percent;
        bar.Fill = Palette.Temperature(this, temp);
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
