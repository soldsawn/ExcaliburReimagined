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
                power?.GpuWatts is { } w ? $"ekran kartı sınırı {w} W" : null,
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
        "Quiet" => "Sessiz: fanlar yavaş başlar, 60 °C'yi geçince hızlanır. Ofis işi ve film için.",
        "Balanced" => "Dengeli: orijinal ayardan daha sessiz, yük altında aynı serinlikte. Günlük kullanım için önerilir.",
        "Performance" => "Performans: fanlar erken ve hızlı döner. Oyun ve uzun süren işler için.",
        "EcAuto" => "Orijinal ayar: fanları dizüstünün kendisi yönetir, uygulama karışmaz.",
        "Manual" => $"Manuel: sabit hız; {s.ManualUntil:HH:mm}'de önceki profile döner.",
        _ => "Kendi oluşturduğunuz profil: fanlar sizin çizdiğiniz eğriyi izler.",
    };
}
