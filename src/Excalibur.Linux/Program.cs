using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Excalibur.Core.Battery;
using Excalibur.Core.Fan;
using Excalibur.Core.History;
using Excalibur.Core.Lighting;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;
using Excalibur.Linux;

// excalibur-reimagined (Linux, L1 + L2): the fan, power, lighting, battery and history service and its CLI.
string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
bool simulate = args.Contains("--simulate");
var rest = args.Where(a => a != "--simulate").ToArray();
var paths = LinuxPaths.For(simulate);

string ProfileKey(string name) => name.ToLowerInvariant() switch
{
    "sessiz" or "quiet" => "Quiet",
    "dengeli" or "balanced" => "Balanced",
    "performans" or "performance" => "Performance",
    "orijinal" or "original" or "ecauto" => "EcAuto",
    _ => name,
};

string ProfileLabel(string profile) => profile switch
{
    "Quiet" => "Sessiz",
    "Balanced" => "Dengeli",
    "Performance" => "Performance" == profile ? "Performans" : profile,
    "EcAuto" => "Orijinal (dizüstünün kendi ayarı)",
    "Manual" => "Manuel",
    _ => profile,
};

PowerMode? ParsePowerMode(string? text) => text?.Trim().ToLowerInvariant() switch
{
    "ofis" or "office" => PowerMode.Office,
    "oyun" or "gaming" or "game" => PowerMode.Gaming,
    "yuksek" or "yüksek" or "high" or "highperformance" or "performans" => PowerMode.HighPerformance,
    _ => null,
};

string ModeLabel(PowerMode? mode) => mode switch
{
    PowerMode.Office => "Ofis",
    PowerMode.Gaming => "Oyun",
    PowerMode.HighPerformance => "Yüksek performans",
    null => "seçilmedi (dizüstünün mevcut ayarı)",
    _ => mode.ToString()!,
};

string EffectLabel(LightEffect effect) => effect switch
{
    LightEffect.Off => "Kapalı",
    LightEffect.Static => "Sabit renk",
    LightEffect.Breathing => "Nefes alma",
    LightEffect.ColorCycle => "Renk döngüsü",
    _ => effect.ToString(),
};

string HistoryModeLabel(HistoryMode mode) => mode switch
{
    HistoryMode.Days7 => "7 gün (diskte)",
    HistoryMode.Days30 => "30 gün (diskte)",
    HistoryMode.Hour1 => "yalnız son 1 saat (bellekte)",
    HistoryMode.Off => "kapalı",
    _ => mode.ToString(),
};

HistoryMode? ParseHistoryMode(string? text) => text?.Trim().ToLowerInvariant() switch
{
    "7d" or "7g" or "days7" or "7" => HistoryMode.Days7,
    "30d" or "30g" or "days30" or "30" => HistoryMode.Days30,
    "1h" or "1s" or "hour1" or "1" => HistoryMode.Hour1,
    "off" or "kapali" or "kapalı" or "kapat" or "0" => HistoryMode.Off,
    _ => null,
};

string NormalizeHexColor(string raw)
{
    string s = raw.Trim();
    return s.StartsWith('#') ? s.ToUpperInvariant() : $"#{s.ToUpperInvariant()}";
}

bool IsOffWord(string? s) => s?.Trim().ToLowerInvariant() is "off" or "kapat" or "kapali" or "kapalı" or "oto" or "auto" or "yok" or "none" or "sifirla" or "sıfırla";

bool TryParseCurve(string text, out IReadOnlyList<CurvePoint> points, out string? error)
{
    var list = new List<CurvePoint>();
    error = null;
    foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var kv = part.Split(':', StringSplitOptions.TrimEntries);
        if (kv.Length != 2
            || !int.TryParse(kv[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tempC)
            || !int.TryParse(kv[1].TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct))
        {
            points = [];
            error = $"Geçersiz eğri noktası '{part}'. Biçim: Sıcaklık:Yüzde (ör. 45:25,60:35,70:60,80:100)";
            return false;
        }
        list.Add(new CurvePoint(tempC, pct));
    }
    points = list;
    if (list.Count == 0)
    {
        error = "En az bir eğri noktası gerekli (ör. 45:25,60:35,70:60,80:100).";
        return false;
    }
    return true;
}

string FormatCurve(IReadOnlyList<CurvePoint>? pts) =>
    pts is null || pts.Count == 0 ? "dizüstünün kendi tablosu" : string.Join(", ", pts.Select(p => $"{p.TempC}°C:%{p.Percent}"));

string FormatMinutes(double totalMinutes)
{
    int mins = (int)Math.Round(totalMinutes);
    return mins >= 60 ? $"{mins / 60} sa {mins % 60:D2} dk" : $"{mins} dk";
}

LinuxStatus? Ask(LinuxRequest request)
{
    try
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(paths.Socket));
        using var stream = new NetworkStream(socket);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8);
        writer.WriteLine(JsonSerializer.Serialize(request, Daemon.Json));
        return JsonSerializer.Deserialize<LinuxStatus>(reader.ReadLine() ?? "", Daemon.Json);
    }
    catch (Exception ex) when (ex is SocketException or IOException or JsonException)
    {
        Console.Error.WriteLine("Fan servisine ulaşılamadı. Çalışıyor mu? sudo systemctl status excalibur-reimagined");
        return null;
    }
}

int PrintProfiles(LinuxStatus? s)
{
    if (s is null) return 2;
    if (s.Error is not null) Console.Error.WriteLine($"Hata: {s.Error}");
    if (s.Profiles is null) return s.Ok ? 0 : 1;
    Console.WriteLine($"Profiller (aktif: {ProfileLabel(s.Profile)}):");
    foreach (var p in s.Profiles)
    {
        string mark = string.Equals(p.Name, s.Profile, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
        string kind = p.BuiltIn ? "yerleşik" : "özel";
        string label = p.BuiltIn ? $"{ProfileLabel(p.Name)} ({p.Name})" : p.Name;
        Console.WriteLine($" {mark} {label,-34} [{kind}]  {FormatCurve(p.Cpu)}");
        if (p.Gpu is not null && p.Cpu is not null && !p.Gpu.SequenceEqual(p.Cpu))
            Console.WriteLine($"   {"",-34}          Ekran kartı: {FormatCurve(p.Gpu)}");
    }
    return s.Ok ? 0 : 1;
}

int PrintBattery(LinuxStatus? s)
{
    if (s is null) return 2;
    if (s.Error is not null) Console.Error.WriteLine($"Hata: {s.Error}");
    if (s.Battery is not { } b) return s.Ok ? 0 : 1;
    Console.WriteLine($"Excalibur Reimagined {version} (Linux) — Pil Durumu");
    if (b.Live is { } live)
    {
        string stateText = live.State switch
        {
            BatteryState.OnBattery => "Pilde çalışıyor",
            BatteryState.Charging => "Şarj oluyor",
            BatteryState.Full => "Tam dolu",
            BatteryState.NotCharging => "Prizde (şarj duraklatıldı)",
            _ => live.State.ToString(),
        };
        string pct = live.Percent is { } p ? $"%{p}" : "?";
        string rem = live.RemainingMinutes is { } m ? $", tahmini kalan: {FormatMinutes(m)}" : "";
        string draw = b.WattsNow is { } w ? $", anlık: {w:F1} W" : "";
        Console.WriteLine($"Durum:        {pct} — {stateText}{draw}{rem}");
    }
    else
    {
        Console.WriteLine("Durum:        Pil bulunamadı");
    }
    if (b.Health is { } h)
    {
        string cyc = h.Cycles is { } c ? $", {c} döngü" : "";
        Console.WriteLine($"Pil sağlığı:  %{h.Percent ?? 0} ({h.FullMWh} / {h.DesignedMWh} mWh, {h.Chemistry ?? "?"}{cyc})");
    }
    if (b.LastInterval is { } last)
        Console.WriteLine($"Son ölçüm:    {last.Watts:F1} W ortalama ({last.Span.TotalMinutes:F0} dk)");
    Console.WriteLine($"Okuma aralığı: pildeyken her {b.ReadMinutes} dakikada bir");
    if (b.Estimates.Count > 0)
    {
        int fullMWh = b.Health?.FullMWh ?? 60000;
        Console.WriteLine("Mod ve profile göre pil tüketimi:");
        foreach (var e in b.Estimates)
        {
            string hours = e.Hours(fullMWh) is { } hr ? $" (~{FormatMinutes(hr * 60)} tam pil süresi)" : "";
            Console.WriteLine($"  {ModeLabel(e.Mode)} + {ProfileLabel(e.Profile)}: {e.Watts:F1} W ortalama ({FormatMinutes(e.Minutes)} veri){hours}");
        }
    }
    return s.Ok ? 0 : 1;
}

int PrintHistory(LinuxStatus? s)
{
    if (s is null) return 2;
    if (s.Error is not null) Console.Error.WriteLine($"Hata: {s.Error}");
    if (s.History is not { } h) return s.Ok ? 0 : 1;
    Console.WriteLine($"Excalibur Reimagined {version} (Linux) — Fan ve Sıcaklık Geçmişi");
    Console.WriteLine($"Saklama modu: {HistoryModeLabel(h.Mode)}");
    Console.WriteLine($"Aralık:       son {FormatMinutes(h.RangeMinutes)}");
    if (h.Summary is { } sum)
    {
        Console.WriteLine($"İşlemci:      ortalama {sum.Cpu.Average} °C, en yüksek {sum.Cpu.Max} °C, ≥{HistorySummary.HotC} °C süre: {FormatMinutes(sum.Cpu.Hot.TotalMinutes)}");
        Console.WriteLine($"Ekran kartı:  ortalama {sum.Gpu.Average} °C, en yüksek {sum.Gpu.Max} °C, ≥{HistorySummary.HotC} °C süre: {FormatMinutes(sum.Gpu.Hot.TotalMinutes)}");
    }
    else
    {
        Console.WriteLine("Bu aralıkta henüz kayıt yok.");
    }
    return s.Ok ? 0 : 1;
}

int Print(LinuxStatus? s)
{
    if (s is null) return 2;
    if (s.Error is not null) Console.Error.WriteLine($"Hata: {s.Error}");
    Console.WriteLine($"Excalibur Reimagined {version} (Linux){(s.Simulated ? " — deneme modu" : "")}");
    if (s.Problem is not null)
    {
        Console.WriteLine($"Fan kontrolü kapalı: {s.Problem}");
        return s.Ok ? 0 : 1;
    }
    string mode = s.Mode switch
    {
        "Running" => "açık",
        "MonitorOnly" => "yalnız izleniyor (bu EC sürümü tanınmadı; denemek için: excalibur-reimagined trial)",
        "Faulted" => "sorun var, fanlar dizüstünün kendi ayarında",
        "Starting" => "başlıyor",
        _ => s.Mode,
    };
    Console.WriteLine($"Fan kontrolü: {mode}");
    Console.WriteLine($"Profil:       {ProfileLabel(s.Profile)}");
    if (s.Power is { } pw)
    {
        string src = pw.OnBattery
            ? $"pilde{(pw.BatteryPercent is { } bp ? $" %{bp}" : "")}"
            : $"prizde{(pw.BatteryPercent is { } wp ? $", pil %{wp}" : "")}";
        string pref = pw.OnBattery && pw.Preferred is { } prefMode && prefMode != PowerMode.Office
            ? $", priz tercihi: {ModeLabel(prefMode)}"
            : "";
        Console.WriteLine($"Güç modu:     {ModeLabel(pw.Mode)} ({src}{pref})");
    }
    Console.WriteLine($"EC sürümü:    {s.EcVersion ?? "?"}");
    if (s.CpuTempC is not null)
    {
        string cpuPower = "";
        if (s.Power is { } cp)
        {
            var parts = new List<string>();
            if (cp.CpuWatts is { } cw) parts.Add($"{cw} W");
            if (cp.CpuLimit is { } ul) parts.Add($"özel sınır: {ul.Pl1Watts}/{ul.Pl2Watts} W");
            else if (cp.CpuPl1Watts is { } pl1) parts.Add($"sınır: {pl1}{(cp.CpuPl2Watts is { } pl2 ? $"/{pl2}" : "")} W");
            if (parts.Count > 0) cpuPower = $"   ({string.Join(", ", parts)})";
        }
        string gpuPower = "";
        if (s.Power is { } gp)
        {
            var parts = new List<string>();
            if (gp.GpuPowerWatts is { } gw) parts.Add(gp.GpuTgpWatts is { } tgp ? $"{gw}/{tgp} W" : $"{gw} W");
            else if (gp.GpuTgpWatts is { } tgpOnly) parts.Add($"tavan {tgpOnly} W");
            if (gp.GpuLimitWatts is { } gl) parts.Add($"özel hedef: {gl} W{(gp.GpuLockMhz is { } mhz ? $" @ {mhz} MHz" : "")}");
            if (parts.Count > 0) gpuPower = $"   ({string.Join(", ", parts)})";
        }
        Console.WriteLine($"İşlemci:      {s.CpuTempC} °C   fan %{s.CpuPercent}  {s.CpuRpm} devir{cpuPower}");
        Console.WriteLine($"Ekran kartı:  {s.GpuTempC} °C   fan %{s.GpuPercent}  {s.GpuRpm} devir{gpuPower}");
    }
    if (s.Lighting is { } l)
    {
        string colors = l.Effect switch
        {
            LightEffect.Off => "",
            LightEffect.ColorCycle => "",
            LightEffect.Breathing => $" {l.Left}",
            _ => l.Left == l.Middle && l.Middle == l.Right ? $" {l.Left}" : $" {l.Left} / {l.Middle} / {l.Right}",
        };
        Console.WriteLine($"Klavye ışığı: {EffectLabel(l.Effect)}{colors} (parlaklık {l.Brightness}/2)");
    }
    if (s.Power?.Automation is { } auto && (auto.Enabled || !auto.IsDefault))
    {
        string acRule = $"{(auto.OnAc.Mode is { } am ? ModeLabel(am) : "-")} / {(auto.OnAc.FanProfile is { } ap ? ProfileLabel(ap) : "-")}";
        string batRule = $"Ofis / {(auto.OnBattery.FanProfile is { } bp ? ProfileLabel(bp) : "-")}";
        Console.WriteLine($"Otomasyon:    {(auto.Enabled ? "açık" : "kapalı")} (prizde: {acRule} · pilde: {batRule})");
    }
    if (s.Trial is not null) Console.WriteLine($"Deneme:       {s.Trial}");
    if (s.Events.Count > 0)
    {
        Console.WriteLine("Son olaylar:");
        foreach (var e in s.Events) Console.WriteLine($"  {e}");
    }
    return s.Ok ? 0 : 1;
}

switch (rest.FirstOrDefault()?.ToLowerInvariant())
{
    case "service":
        return Daemon.Run(simulate);

    case null:
    case "status":
    case "durum":
        return Print(Ask(new LinuxRequest("status")));

    case "profiles":
    case "profiller":
        return PrintProfiles(Ask(new LinuxRequest("profiles")));

    case "profile":
    case "profil":
    {
        if (rest.Length < 2)
        {
            Console.Error.WriteLine("""
                Kullanım:
                  exr profile <ad>                                  sessiz | dengeli | performans | orijinal | <özel-ad>
                  exr profile list                                  tüm profilleri ve eğrilerini göster
                  exr profile save <ad> <noktalar> [--gpu <nokta>]  özel profil kaydet (ör. 45:25,60:35,70:60,80:100)
                  exr profile delete <ad>                           özel profili sil
                """);
            return 2;
        }
        string sub = rest[1].ToLowerInvariant();
        if (sub is "list" or "liste")
            return PrintProfiles(Ask(new LinuxRequest("profiles")));

        if (sub is "save" or "kaydet")
        {
            if (rest.Length < 4)
            {
                Console.Error.WriteLine("Kullanım: exr profile save <ad> <sıcaklık:yüzde,...> [--gpu <sıcaklık:yüzde,...>]");
                Console.Error.WriteLine("Örnek:    exr profile save Gece 45:25,60:35,70:60,80:100");
                return 2;
            }
            string name = rest[2];
            if (!TryParseCurve(rest[3], out var cpuCurve, out string? err))
            {
                Console.Error.WriteLine($"Hata: {err}");
                return 2;
            }
            IReadOnlyList<CurvePoint>? gpuCurve = null;
            if (rest.Length >= 6 && rest[4].ToLowerInvariant() is "--gpu" or "gpu")
            {
                if (!TryParseCurve(rest[5], out var parsedGpu, out string? gpuErr))
                {
                    Console.Error.WriteLine($"Hata (ekran kartı eğrisi): {gpuErr}");
                    return 2;
                }
                gpuCurve = parsedGpu;
            }
            return PrintProfiles(Ask(new LinuxRequest("saveProfile", CustomProfile: new CustomProfile(name, cpuCurve, gpuCurve))));
        }

        if (sub is "delete" or "sil" or "remove")
        {
            if (rest.Length < 3)
            {
                Console.Error.WriteLine("Kullanım: exr profile delete <özel-profil-adı>");
                return 2;
            }
            return PrintProfiles(Ask(new LinuxRequest("deleteProfile", Profile: rest[2])));
        }

        return Print(Ask(new LinuxRequest("setProfile", ProfileKey(rest[1]))));
    }

    case "manual":
    case "manuel":
    {
        if (rest.Length < 2 || !int.TryParse(rest[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cpuPct))
        {
            Console.Error.WriteLine("Kullanım: exr manual <cpu%> [gpu%] [dakika]  (1–10 dk, ör. exr manual 60 veya exr manual 60 70 5)");
            return 2;
        }
        int gpuPct = rest.Length >= 3 && int.TryParse(rest[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int g) ? g : cpuPct;
        int mins = rest.Length >= 4 && int.TryParse(rest[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ? m : 10;
        return Print(Ask(new LinuxRequest("setManual", CpuPercent: cpuPct, GpuPercent: gpuPct, Minutes: mins)));
    }

    case "mode":
    case "mod":
    case "power":
    case "guc":
    case "güç":
    {
        if (rest.Length < 2 || ParsePowerMode(rest[1]) is not { } pmode)
        {
            Console.Error.WriteLine("Kullanım: exr mode ofis|oyun|yuksek  (office|gaming|high)");
            return 2;
        }
        return Print(Ask(new LinuxRequest("setPowerMode", PowerMode: pmode)));
    }

    case "cpu-limit":
    case "cpulimit":
    {
        if (rest.Length == 2 && IsOffWord(rest[1]))
            return Print(Ask(new LinuxRequest("setCpuLimit", CpuLimit: null)));
        if (rest.Length < 3
            || !int.TryParse(rest[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pl1)
            || !int.TryParse(rest[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pl2))
        {
            Console.Error.WriteLine("Kullanım: exr cpu-limit <uzun-süreli-W> <kısa-süreli-W>  (15–55 W ve 30–115 W, 5'in katı; ör. exr cpu-limit 35 60)");
            Console.Error.WriteLine("          exr cpu-limit off                              (özel sınırı kaldırır, güç moduna bırakır)");
            return 2;
        }
        return Print(Ask(new LinuxRequest("setCpuLimit", CpuLimit: new CpuLimit(pl1, pl2))));
    }

    case "gpu-limit":
    case "gpulimit":
    {
        if (rest.Length == 2 && IsOffWord(rest[1]))
            return Print(Ask(new LinuxRequest("setGpuLimit", GpuLimitWatts: null)));
        if (rest.Length < 2 || !int.TryParse(rest[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int watts))
        {
            Console.Error.WriteLine("Kullanım: exr gpu-limit <hedef-W>  (30–175 W, 5'in katı; ör. exr gpu-limit 60)");
            Console.Error.WriteLine("          exr gpu-limit off        (saat kilidini kaldırır, güç moduna bırakır)");
            return 2;
        }
        return Print(Ask(new LinuxRequest("setGpuLimit", GpuLimitWatts: watts)));
    }

    case "auto":
    case "otomasyon":
    {
        var currentStatus = Ask(new LinuxRequest("status"));
        if (currentStatus is null) return 2;
        var auto = currentStatus.Power?.Automation ?? new PowerAutomation();
        if (rest.Length < 2)
            return Print(currentStatus);

        string sub = rest[1].ToLowerInvariant();
        if (sub is "on" or "acik" or "açık" or "enable")
            return Print(Ask(new LinuxRequest("setAutomation", Automation: auto with { Enabled = true })));
        if (sub is "off" or "kapali" or "kapalı" or "disable" or "kapat")
            return Print(Ask(new LinuxRequest("setAutomation", Automation: auto with { Enabled = false })));

        if (sub is "ac" or "priz" or "bat" or "pil" or "battery")
        {
            bool isAc = sub is "ac" or "priz";
            var rule = isAc ? auto.OnAc : auto.OnBattery;
            if (rest.Length < 4)
            {
                Console.Error.WriteLine(isAc
                    ? "Kullanım: exr auto ac [mode ofis|oyun|yuksek|yok] [profile <ad>|yok]"
                    : "Kullanım: exr auto bat profile <ad>|yok   (pilde güç modu her zaman Ofis'tir)");
                return 2;
            }
            for (int i = 2; i + 1 < rest.Length; i += 2)
            {
                string key = rest[i].ToLowerInvariant();
                string val = rest[i + 1];
                if (key is "mode" or "mod")
                {
                    if (!isAc)
                    {
                        Console.Error.WriteLine("Hata: Pilde yalnız Ofis modu kullanılabilir.");
                        return 2;
                    }
                    if (IsOffWord(val)) rule = rule with { Mode = null };
                    else if (ParsePowerMode(val) is { } m) rule = rule with { Mode = m };
                    else { Console.Error.WriteLine($"Hata: Bilinmeyen güç modu '{val}'."); return 2; }
                }
                else if (key is "profile" or "profil")
                {
                    rule = rule with { FanProfile = IsOffWord(val) ? null : ProfileKey(val) };
                }
                else
                {
                    Console.Error.WriteLine($"Hata: Bilinmeyen seçenek '{rest[i]}'. 'mode' veya 'profile' kullanın.");
                    return 2;
                }
            }
            var updated = isAc
                ? auto with { Enabled = true, OnAc = rule }
                : auto with { Enabled = true, OnBattery = rule with { Mode = PowerMode.Office } };
            return Print(Ask(new LinuxRequest("setAutomation", Automation: updated)));
        }

        Console.Error.WriteLine("""
            Kullanım:
              exr auto on | off                                        priz/pil otomasyonunu aç veya kapat
              exr auto ac [mode ofis|oyun|yuksek|yok] [profile <ad>]   şarja takılınca uygulanacak kural
              exr auto bat profile <ad>|yok                            pile geçince uygulanacak fan profili
            """);
        return 2;
    }

    case "light":
    case "isik":
    case "ışık":
    case "led":
    {
        if (rest.Length < 2)
            return Print(Ask(new LinuxRequest("status")));

        if (rest[1].ToLowerInvariant() is "help" or "--help" or "-h" or "yardim" or "yardım")
        {
            Console.WriteLine("""
                Kullanım:
                  exr light #RRGGBB [--level 0|1|2]                    tüm klavye tek sabit renk (ör. exr light #00FFFF)
                  exr light [zones] #Sol #Orta #Sağ [--level 0|1|2]    üç bölge ayrı sabit renk
                  exr light breathing #RRGGBB [--level 0|1|2]          nefes alma efekti
                  exr light cycle [--level 0|1|2]                      renk döngüsü efekti
                  exr light level 0|1|2                                parlaklık kademesi (0 kapalı, 1 düşük, 2 yüksek)
                  exr light idle 0|1|5|15                              boşta kalınca kapatma süresi (dakika, 0 = kapalı)
                  exr light off                                        klavye ışığını kapat
                """);
            return 0;
        }
        var current = Ask(new LinuxRequest("status"))?.Lighting ?? new KeyboardLighting();
        var tokens = rest.Skip(1).ToList();
        int brightness = current.Brightness;
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i].ToLowerInvariant() is "--level" or "-l")
            {
                if (!int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out brightness) || brightness is < 0 or > 2)
                {
                    Console.Error.WriteLine("Hata: Parlaklık 0, 1 veya 2 olmalıdır.");
                    return 2;
                }
                tokens.RemoveRange(i, 2);
                break;
            }
        }
        if (tokens.Count == 0)
            return Print(Ask(new LinuxRequest("setLighting", Lighting: current with { Brightness = brightness })));

        string first = tokens[0].ToLowerInvariant();
        if (first is "zones" or "bolgeler" or "bölgeler")
            tokens.RemoveAt(0);
        if (tokens.Count == 0) return 2;
        first = tokens[0].ToLowerInvariant();

        KeyboardLighting next;
        if (IsOffWord(first))
        {
            next = current with { Effect = LightEffect.Off };
        }
        else if (first is "idle" or "bosta" or "boşta")
        {
            int idleMins = tokens.Count >= 2 && IsOffWord(tokens[1]) ? 0
                : (tokens.Count >= 2 && int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int im) ? im : -1);
            if (idleMins < 0)
            {
                Console.Error.WriteLine("Kullanım: exr light idle 0|1|5|15");
                return 2;
            }
            next = current with { IdleOffMinutes = idleMins };
        }
        else if (first is "level" or "parlaklik" or "parlaklık")
        {
            if (tokens.Count < 2 || !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lvl) || lvl is < 0 or > 2)
            {
                Console.Error.WriteLine("Kullanım: exr light level 0|1|2");
                return 2;
            }
            next = current with { Brightness = lvl, Effect = lvl == 0 ? LightEffect.Off : (current.Effect == LightEffect.Off ? LightEffect.Static : current.Effect) };
        }
        else if (first is "cycle" or "dongu" or "döngü" or "colorcycle")
        {
            next = current with { Effect = LightEffect.ColorCycle, Brightness = brightness == 0 ? 1 : brightness };
        }
        else if (first is "breathing" or "nefes")
        {
            string hex = tokens.Count >= 2 ? NormalizeHexColor(tokens[1]) : current.Left;
            next = current with { Effect = LightEffect.Breathing, Left = hex, Middle = hex, Right = hex, Brightness = brightness == 0 ? 1 : brightness };
        }
        else if (tokens.Count >= 3)
        {
            next = current with
            {
                Effect = LightEffect.Static,
                Left = NormalizeHexColor(tokens[0]),
                Middle = NormalizeHexColor(tokens[1]),
                Right = NormalizeHexColor(tokens[2]),
                Brightness = brightness == 0 ? 1 : brightness,
            };
        }
        else
        {
            string hex = NormalizeHexColor(tokens[0]);
            next = current with
            {
                Effect = LightEffect.Static,
                Left = hex,
                Middle = hex,
                Right = hex,
                Brightness = brightness == 0 ? 1 : brightness,
            };
        }
        return Print(Ask(new LinuxRequest("setLighting", Lighting: next)));
    }

    case "battery":
    case "pil":
    {
        if (rest.Length >= 3 && rest[1].ToLowerInvariant() is "interval" or "aralik" or "aralık")
        {
            if (!int.TryParse(rest[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mins))
            {
                Console.Error.WriteLine("Kullanım: exr battery interval 2|5|10|30");
                return 2;
            }
            return PrintBattery(Ask(new LinuxRequest("setBatteryInterval", BatteryReadMinutes: mins)));
        }
        return PrintBattery(Ask(new LinuxRequest("battery")));
    }

    case "history":
    case "gecmis":
    case "geçmiş":
    {
        if (rest.Length >= 3 && rest[1].ToLowerInvariant() is "mode" or "mod")
        {
            if (ParseHistoryMode(rest[2]) is not { } hm)
            {
                Console.Error.WriteLine("Kullanım: exr history mode 7g|30g|1s|kapali  (7d|30d|1h|off)");
                return 2;
            }
            return PrintHistory(Ask(new LinuxRequest("setHistoryMode", HistoryMode: hm)));
        }
        int mins = rest.Length >= 2 && int.TryParse(rest[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ? m : 60;
        return PrintHistory(Ask(new LinuxRequest("history", Minutes: mins)));
    }

    case "sim-power":
    {
        if (rest.Length < 2 || rest[1].ToLowerInvariant() is not ("ac" or "priz" or "bat" or "pil"))
        {
            Console.Error.WriteLine("Kullanım (yalnız --simulate): exr --simulate sim-power ac|bat");
            return 2;
        }
        bool onBat = rest[1].ToLowerInvariant() is "bat" or "pil";
        return Print(Ask(new LinuxRequest("simPower", OnBattery: onBat)));
    }

    case "trial":
    case "deneme":
        Console.WriteLine("Deneme, fanlara kısa süre bir hız yazıp uyup uymadıklarına bakar (yaklaşık bir dakika); olmazsa fanlar hemen");
        Console.WriteLine("dizüstünün kendi ayarına döner. Geçerse bu bilgisayarda fan kontrolü açılır. Başlatılsın mı? [e/H]");
        if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("e" or "evet" or "y"))
            return 0;
        return Print(Ask(new LinuxRequest("trial")));

    case "sleep":
        // systemd's sleep hook: "pre" before suspend, "post" after resume.
        Ask(new LinuxRequest(rest.ElementAtOrDefault(1) == "post" ? "wake" : "sleep"));
        return 0;

    case "release":
        // systemd runs this after the service stops (ExecStopPost), also after a crash: both fans back to the EC.
        if (simulate) return 0;
        using (var ec = LinuxEc.Open(out string? problem))
        {
            if (ec is null) { Console.Error.WriteLine(problem); return 1; }
            ec.Write(FanRegisters.CpuTarget, FanTarget.AutoValue);
            ec.Write(FanRegisters.GpuTarget, FanTarget.AutoValue);
        }
        return 0;

    case "report":
    case "rapor":
    {
        var s = Ask(new LinuxRequest("status"));
        string summary = $"Excalibur Reimagined {version} (Linux) · EC: {s?.EcVersion ?? "?"} · Mod: {s?.Mode ?? "servis yok"}" +
                         (s?.Problem is { } p ? $" · Sorun: {p}" : "") + $" · Çekirdek: {Environment.OSVersion.VersionString}";
        string home = Environment.GetEnvironmentVariable("SUDO_USER") is { Length: > 0 } user ? $"/home/{user}" : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string zip = BugReport.Create(home, paths.Data, summary, [], DateTime.Now, paths.Logs);
        Console.WriteLine($"Rapor hazır: {zip}");
        Console.WriteLine($"Şu sayfayı açıp neyin çalışmadığını yazın ve zip'i sürükleyin: {BugReport.IssuesUrl}");
        return 0;
    }

    case "version":
    case "--version":
        Console.WriteLine($"Excalibur Reimagined {version} (Linux)");
        return 0;

    default:
        Console.WriteLine($"""
            Excalibur Reimagined {version} (Linux) — Casper Excalibur kontrol uygulaması

              exr status                                       durum: sıcaklıklar, fanlar, güç modu, watt sınırları
              exr profile <ad>                                 sessiz | dengeli | performans | orijinal | <özel-ad>
              exr profiles                                     tüm profilleri ve fan eğrilerini listeler
              exr profile save <ad> <eğri> [--gpu <eğri>]      özel fan profili kaydeder (ör. 45:25,60:35,70:60,80:100)
              exr profile delete <ad>                          özel fan profilini siler
              exr manual <cpu%> [gpu%] [dakika]                fanları geçici olarak sabit yüzdede tutar
              exr mode <ofis|oyun|yuksek>                      güç modunu değiştirir (pilde yalnız Ofis)
              exr cpu-limit <pl1> <pl2> | off                  işlemci uzun/kısa süreli watt sınırı (15–55 / 30–115 W)
              exr gpu-limit <watt> | off                       ekran kartı watt hedefi (30–175 W)
              exr auto on | off | ac ... | bat ...             şarja takılınca / pile geçince otomatik mod ve profil
              exr light #RRGGBB | breathing | cycle | off      klavye aydınlatması (3 bölge renk, efekt, parlaklık 0–2)
              exr battery [interval 2|5|10|30]                 pil durumu, sağlık ve süre tahminleri
              exr history [dakika | mode 7g|30g|1s|kapali]     fan ve sıcaklık geçmişi özeti
              exr trial                                        tanınmayan EC sürümünde kısa deneme (onay ister)
              exr report                                       sorun bildirmek için günlükleri bir zip'e koyar
            """);
        return rest.Length == 0 ? 0 : 2;
}
