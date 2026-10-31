using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Excalibur.Core.Fan;
using Excalibur.Core.Settings;
using Excalibur.Linux;

// excalibur-reimagined (Linux, L1): the fan service and the commands that talk to it over a local socket.
string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
bool simulate = args.Contains("--simulate");
var rest = args.Where(a => a != "--simulate").ToArray();
var paths = LinuxPaths.For(simulate);

string Profile(string name) => name.ToLowerInvariant() switch
{
    "sessiz" or "quiet" => "Quiet",
    "dengeli" or "balanced" => "Balanced",
    "performans" or "performance" => "Performance",
    "orijinal" or "original" or "ecauto" => "EcAuto",
    _ => name,
};

string Label(string profile) => profile switch
{
    "Quiet" => "Sessiz",
    "Balanced" => "Dengeli",
    "Performance" => "Performans",
    "EcAuto" => "Orijinal (dizüstünün kendi ayarı)",
    _ => profile,
};

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
    Console.WriteLine($"Profil:       {Label(s.Profile)}");
    Console.WriteLine($"EC sürümü:    {s.EcVersion ?? "?"}");
    if (s.CpuTempC is not null)
    {
        Console.WriteLine($"İşlemci:      {s.CpuTempC} °C   fan %{s.CpuPercent}  {s.CpuRpm} devir");
        Console.WriteLine($"Ekran kartı:  {s.GpuTempC} °C   fan %{s.GpuPercent}  {s.GpuRpm} devir");
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

    case "profile":
    case "profil":
        if (rest.Length < 2)
        {
            Console.Error.WriteLine("Kullanım: excalibur-reimagined profile sessiz|dengeli|performans|orijinal");
            return 2;
        }
        return Print(Ask(new LinuxRequest("setProfile", Profile(rest[1]))));

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
            Excalibur Reimagined {version} (Linux) — Casper Excalibur fan kontrolü

              excalibur-reimagined status                     durum: sıcaklıklar, fanlar, profil
              excalibur-reimagined profile <ad>               sessiz | dengeli | performans | orijinal
              excalibur-reimagined trial                      tanınmayan EC sürümünde kısa deneme (onay ister)
              excalibur-reimagined report                     sorun bildirmek için günlükleri bir zip'e koyar

            Kısa yol: exr (ör. exr profile sessiz)
            """);
        return rest.Length == 0 ? 0 : 2;
}
