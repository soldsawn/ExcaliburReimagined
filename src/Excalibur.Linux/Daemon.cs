using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Excalibur.Core.Fan;
using Excalibur.Core.Settings;

namespace Excalibur.Linux;

/// <summary>Where the Linux version keeps its files (the simulation in /tmp, so it runs without root).</summary>
internal sealed record LinuxPaths(string Data, string Logs, string Socket)
{
    public static LinuxPaths For(bool simulate) => simulate
        ? new("/tmp/excalibur-reimagined-sim", "/tmp/excalibur-reimagined-sim/logs", "/tmp/excalibur-reimagined-sim/control.sock")
        : new("/var/lib/excalibur-reimagined", "/var/log/excalibur-reimagined", "/run/excalibur-reimagined.sock");

    public string Settings => Path.Combine(Data, "fan.json");
}

/// <summary>A request on the control socket: one JSON line.</summary>
internal sealed record LinuxRequest(string Cmd, string? Profile = null);

/// <summary>The answer: the loop's state as the command line prints it.</summary>
internal sealed record LinuxStatus(
    bool Ok, string? Error, string Mode, string Profile, string? EcVersion, bool Simulated, string? Problem,
    int? CpuTempC, int? GpuTempC, int? CpuRpm, int? GpuRpm, int? CpuPercent, int? GpuPercent, bool HoldsFans,
    string? Trial, IReadOnlyList<string> Events);

/// <summary>
/// The fan service (L1): one tick a second of the same <see cref="FanControlLoop"/> as on Windows (curves, safety floors,
/// hot rule, EC version list and trial), the fans handed to the EC on SIGTERM, on sleep (systemd's sleep hook) and when
/// anything unexpected happens. When the EC cannot be written (Secure Boot) it keeps answering with the reason instead of
/// exiting, so systemd does not restart it in a loop.
/// </summary>
internal sealed class Daemon
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly LinuxPaths _paths;
    private readonly bool _simulate;
    private readonly FanFileLog _log;
    private readonly ServiceSettingsStore _settings;
    private readonly FanControlLoop? _loop;
    private readonly FakeFanHardware? _fake;
    private readonly string? _problem;
    private readonly object _gate = new();
    private bool _asleep;
    private FanStatus? _last;

    private Daemon(bool simulate)
    {
        _simulate = simulate;
        _paths = LinuxPaths.For(simulate);
        Directory.CreateDirectory(_paths.Data);
        Directory.CreateDirectory(_paths.Logs);
        _log = new FanFileLog(_paths.Logs, "fan");
        _settings = new ServiceSettingsStore(_paths.Settings);

        IFanHardware? hardware;
        if (simulate) hardware = _fake = new FakeFanHardware();
        else hardware = LinuxEc.Open(out _problem) is { } ec ? new LinuxFanHardware(ec, Log) : null;
        if (hardware is null) return;

        _loop = new FanControlLoop(hardware, new FanLoopOptions
        {
            KnownEcVersions = new HashSet<string>(new FanLoopOptions().KnownEcVersions.Concat(_settings.Current.TrustedEcVersions)),
        });
        // A version that passed the trial stays trusted after a restart (as on Windows).
        _loop.EcTrusted += (v, profile, path) => _settings.Update(s => s with
        {
            TrustedEcVersions = s.TrustedEcVersions.Contains(v) ? s.TrustedEcVersions : [.. s.TrustedEcVersions, v],
            Profile = profile.Name,
        }, out _);
    }

    public static int Run(bool simulate)
    {
        var daemon = new Daemon(simulate);
        using var stop = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });
        return daemon.Serve(stop.Token);
    }

    private int Serve(CancellationToken stop)
    {
        Log($"service started ({(_simulate ? "SIMULATION" : "real EC")}){(_problem is null ? "" : $": {_problem}")}");
        var server = Task.Run(() => ListenAsync(stop), stop);
        try
        {
            if (_loop is not null)
            {
                lock (_gate) _loop.Start(SavedProfile(), DateTimeOffset.Now);
                DrainEvents();
            }
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            int ticks = 0;
            while (timer.WaitForNextTickAsync(stop).AsTask().GetAwaiter().GetResult())
            {
                if (_loop is null) continue;
                lock (_gate)
                {
                    if (_asleep) continue;
                    _fake?.Advance(TimeSpan.FromSeconds(1));
                    _last = _loop.Tick(DateTimeOffset.Now);
                }
                DrainEvents();
                if (++ticks % 60 == 0 && _last?.Snapshot is { } s)
                    Log($"status {_last.Mode} {_last.Profile}: CPU {s.CpuTempC}C {s.CpuRpm}rpm {s.CpuTarget}, GPU {s.GpuTempC}C {s.GpuRpm}rpm {s.GpuTarget}");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log($"!! unexpected error, handing fans to the EC: {ex}");
            HandBack();
            return 1;
        }
        HandBack();
        Log("service stopped, fans handed to the EC");
        return 0;
    }

    private void HandBack()
    {
        if (_loop is null) return;
        lock (_gate) _loop.Stop(DateTimeOffset.Now);
        DrainEvents();
    }

    private FanProfile SavedProfile() => _settings.Current.FindProfile(_settings.Current.Profile) ?? FanProfile.Balanced;

    private async Task ListenAsync(CancellationToken stop)
    {
        if (File.Exists(_paths.Socket)) File.Delete(_paths.Socket);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(_paths.Socket));
        // Local users may ask for the status and change the profile (as with the Windows pipe); the fans stay under the safety rules.
        File.SetUnixFileMode(_paths.Socket, (UnixFileMode)0b110_110_110);
        listener.Listen(8);
        while (!stop.IsCancellationRequested)
        {
            var client = await listener.AcceptAsync(stop);
            _ = Task.Run(() => Handle(client));
        }
    }

    private void Handle(Socket client)
    {
        using (client)
        using (var stream = new NetworkStream(client))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
        {
            LinuxStatus answer;
            try
            {
                var req = JsonSerializer.Deserialize<LinuxRequest>(reader.ReadLine() ?? "", Json);
                answer = req?.Cmd is null ? Status("boş istek") : Execute(req);
            }
            catch (JsonException) { answer = Status("geçersiz istek"); }
            writer.WriteLine(JsonSerializer.Serialize(answer, Json));
        }
    }

    private LinuxStatus Execute(LinuxRequest req)
    {
        var now = DateTimeOffset.Now;
        if (_loop is null) return Status(req.Cmd == "status" ? null : _problem);
        switch (req.Cmd.ToLowerInvariant())
        {
            case "status":
                return Status(null);
            case "setprofile":
                if (req.Profile is null || _settings.Current.FindProfile(req.Profile) is not { } profile) return Status($"bilinmeyen profil: {req.Profile}");
                lock (_gate) _loop.SetProfile(profile, now, "cli");
                _settings.Update(s => s with { Profile = profile.Name }, out _);
                return Status(null);
            case "trial":
                lock (_gate) _loop.StartEcTrial(now);
                return Status(null);
            case "sleep":
                lock (_gate) { _asleep = true; _loop.Stop(now); }
                Log("suspending: fans handed to the EC");
                return Status(null);
            case "wake":
                lock (_gate) { _asleep = false; _loop.Start(SavedProfile(), now); }
                Log("resumed: fan control restarted");
                return Status(null);
            default:
                return Status($"bilinmeyen komut: {req.Cmd}");
        }
    }

    private LinuxStatus Status(string? error)
    {
        FanStatus? s;
        IReadOnlyList<string> events;
        lock (_gate)
        {
            // The loop's own status: right after a command it already shows the change (the last tick's may not).
            s = _loop?.Status ?? _last;
            events = _loop?.Events.TakeLast(8).Select(e => $"{e.At:HH:mm:ss} {e.Message}").ToList() ?? [];
        }
        var snap = s?.Snapshot;
        return new LinuxStatus(error is null, error, s?.Mode.ToString() ?? (_loop is null ? "Unavailable" : "Starting"), s?.Profile ?? _settings.Current.Profile,
            s?.EcVersion, _simulate, _problem, snap?.CpuTempC, snap?.GpuTempC, snap?.CpuRpm, snap?.GpuRpm,
            snap?.CpuAppliedPercent, snap?.GpuAppliedPercent, s?.HoldsFans ?? false,
            s?.Trial is { } t ? $"{t.State}{(t.Problem is null ? "" : $" ({t.Problem})")}" : null, events);
    }

    private long _logged;

    private void DrainEvents()
    {
        if (_loop is null) return;
        // The loop keeps only the latest events; TotalEvents counts all of them (as the Windows worker reads it).
        long total = _loop.TotalEvents;
        if (total == _logged) return;
        var events = _loop.Events;
        long first = total - events.Count;
        foreach (var (e, i) in events.Select((e, i) => (e, first + i)))
            if (i >= _logged) _log.Write(e.At, e.Message);
        _logged = total;
    }

    private void Log(string message)
    {
        _log.Write(DateTimeOffset.Now, message);
        Console.WriteLine(message);
    }
}
