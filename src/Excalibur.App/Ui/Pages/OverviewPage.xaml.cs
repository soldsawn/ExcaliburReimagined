using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Excalibur.App.Tray;
using Excalibur.App.Ui.Controls;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;

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
                $"{(t.Fan == FanId.Gpu ? "Ekran kartı" : "İşlemci")} fanı birkaç saniye farklı hızda dönecek. Deneme bitince fanlar orijinal ayara döner."),
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
        if (_trialError is not null) TrialText.Text += $" ({_trialError})";
        StartTrial.Visibility = unknown && s.EcVersion is not null && t?.State != EcTrialState.Running ? Visibility.Visible : Visibility.Collapsed;
        StartTrial.Content = t?.State == EcTrialState.Failed ? "Tekrar dene" : s.EcSeenElsewhere ? "Başlat" : "Dene";
        CloseTrial.Visibility = t?.State == EcTrialState.Passed ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task StartTrialAsync()
    {
        string firmware = _connection.Status?.EcVersion ?? "?";
        // A version that already passed elsewhere: the card says what happens and its button is the consent.
        var answer = _connection.Status?.EcSeenElsewhere == true ? MessageBoxResult.Yes : MessageBox.Show(
            $"Bu fan yazılımı sürümü ({firmware}) henüz denenmedi.\n\n" +
            "Deneme yaklaşık yarım dakika sürer: önce işlemci, sonra ekran kartı fanı birkaç saniye farklı hızda döner ve hızın gerçekten " +
            "değişip değişmediğine bakılır. Her fan denendikten sonra orijinal ayara döner. Dizüstü 75 °C'nin altındayken başlar, " +
            "80 °C'yi geçerse durur.\n\nDenemeyi başlatmak istiyor musunuz?",
            "Fan kontrolünü dene", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
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
               (t is null ? "" : $" · işlemci fanı {Fan(t.CpuFollows)}, ekran kartı fanı {Fan(t.GpuFollows)}");
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
        int? percent = target is null ? null : applied ?? target;
        duty.Text = rpm is null ? "" : percent is { } p ? $"%{p}" : "EC";
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
