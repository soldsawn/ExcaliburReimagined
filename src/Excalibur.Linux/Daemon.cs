using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Excalibur.Core.Battery;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Hardware;
using Excalibur.Core.History;
using Excalibur.Core.Lighting;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.Linux;

/// <summary>Where the Linux version keeps its files (the simulation in /tmp, so it runs without root).</summary>
internal sealed record LinuxPaths(string Data, string Logs, string Socket)
{
    public static LinuxPaths For(bool simulate) => simulate
        ? new("/tmp/excalibur-reimagined-sim", "/tmp/excalibur-reimagined-sim/logs", "/tmp/excalibur-reimagined-sim/control.sock")
        : new("/var/lib/excalibur-reimagined", "/var/log/excalibur-reimagined", "/run/excalibur-reimagined.sock");

    public string Settings => Path.Combine(Data, "fan.json");
    public string PowerModes => Path.Combine(Data, "power-modes.txt");
    public string History => Path.Combine(Data, "history");
    public string Battery => Path.Combine(Data, "battery");
}

/// <summary>
/// The fan, power, lighting, battery and history service on Linux (L1 + L2): one tick a second of the same
/// <see cref="FanControlLoop"/> as on Windows (curves, safety floors, hot rule, EC version list and trial), plus
/// <see cref="PowerManager"/> every 2 s, <see cref="LightingManager"/> (EC LED registers), <see cref="HistoryRecorder"/> and
/// <see cref="BatteryMonitor"/>/<see cref="BatteryRecorder"/>. The fans are handed to the EC and CPU/GPU limits restored on
/// SIGTERM, on sleep (systemd's sleep hook) and when anything unexpected happens.
/// </summary>
internal sealed class Daemon
{
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(5);

    private readonly LinuxPaths _paths;
    private readonly bool _simulate;
    private readonly FanFileLog _log;
    private readonly ServiceSettingsStore _settings;
    private readonly HistoryRecorder _history;
    private readonly BatteryMonitor _battery;
    private readonly BatteryRecorder _batteryRecorder;
    private readonly BatteryCsvLog _batteryCsv;
    private readonly BatteryEstimates _batteryEstimates;
    private readonly LinuxBatteryHardware? _linuxBattery;
    private readonly SimulatedBatteryHardware? _simBattery;
    private readonly FanControlLoop? _loop;
    private readonly ProfileSwitcher? _switcher;
    private readonly PowerManager? _power;
    private readonly LightingManager? _lighting;
    private readonly LinuxEcSmiChannel? _smi;
    private readonly FakeFanHardware? _fake;
    private readonly SimulatedPowerHardware? _simPower;
    private readonly string? _problem;
    private readonly FanCommandHandler? _commands;
    private readonly object _gate = new();
    private bool _asleep;
    private DateTimeOffset? _resumeAt;
    private DateTimeOffset _healthAt;
    private int? _lastLightKeyLevel;
    private FanStatus? _last;

    private Daemon(bool simulate)
    {
        _simulate = simulate;
        _paths = LinuxPaths.For(simulate);
        Directory.CreateDirectory(_paths.Data);
        Directory.CreateDirectory(_paths.Logs);
        _log = new FanFileLog(_paths.Logs, "fan");
        _settings = new ServiceSettingsStore(_paths.Settings);
        _history = new HistoryRecorder(_paths.History, _settings.Current.History, Log);

        IBatteryHardware batHw;
        IBatteryMeter batMeter;
        if (simulate)
        {
            _simBattery = new SimulatedBatteryHardware();
            batHw = _simBattery;
            batMeter = _simBattery;
        }
        else
        {
            _linuxBattery = new LinuxBatteryHardware();
            batHw = _linuxBattery;
            batMeter = _linuxBattery;
        }
        _battery = new BatteryMonitor(batHw, Log);
        _batteryCsv = new BatteryCsvLog(_paths.Battery);
        _batteryEstimates = BatteryEstimates.Load(_paths.Battery, Log);
        _batteryRecorder = new BatteryRecorder(batMeter) { Interval = TimeSpan.FromMinutes(_settings.Current.BatteryReadMinutes) };
        _batteryRecorder.Recorded += OnBatteryIntervalRecorded;
        _healthAt = DateTimeOffset.Now.AddSeconds(simulate ? 0 : 10);

        IFanHardware? hardware;
        IPowerHardware? powerHardware = null;
        ISmiChannel? smiChannel = null;
        if (simulate)
        {
            hardware = _fake = new FakeFanHardware();
            powerHardware = _simPower = new SimulatedPowerHardware();
            smiChannel = new SimulatedSmiChannel();
        }
        else if (LinuxEc.Open(out _problem) is { } ec)
        {
            hardware = new LinuxFanHardware(ec, Log);
            powerHardware = new LinuxPowerHardware(ec, new LinuxRapl(), new GpuLimiter(new NvmlGpuClocks()));
            smiChannel = _smi = new LinuxEcSmiChannel(ec, Log);
        }
        else
        {
            hardware = null;
        }
        if (hardware is null) return;

        _loop = new FanControlLoop(hardware, new FanLoopOptions
        {
            KnownEcVersions = new HashSet<string>(new FanLoopOptions().KnownEcVersions.Concat(_settings.Current.TrustedEcVersions)),
        });
        _switcher = new ProfileSwitcher(_loop, _settings, Log);
        if (powerHardware is not null)
        {
            var modes = new PowerModeWatts(_paths.PowerModes);
            _power = new PowerManager(powerHardware, _settings, (name, src) =>
            {
                lock (_gate) _switcher.Switch(name, src);
            }, Log, modes: modes);
        }
        if (smiChannel is not null)
            _lighting = new LightingManager(new KeyboardLight(smiChannel), _settings, Log);

        // A version that passed the trial stays trusted after a restart (as on Windows).
        _loop.EcTrusted += (v, profile, path) => _settings.Update(s => s with
        {
            TrustedEcVersions = s.TrustedEcVersions.Contains(v) ? s.TrustedEcVersions : [.. s.TrustedEcVersions, v],
            Profile = profile.Name,
        }, out _);
        _settings.Changed += s => _history.Mode = s.History;

        // The same commands as the Windows service's pipe (no Casper clean-up, no "shutdown": systemd owns the service).
        if (_power is not null && _lighting is not null)
            _commands = new FanCommandHandler(_loop, simulate, DateTimeOffset.Now, _settings, _history, _power, _lighting, _battery,
                _batteryRecorder, _batteryEstimates, _switcher, _log, warn: Log);
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
                var now = DateTimeOffset.Now;
                lock (_gate)
                {
                    _loop.Start(SavedProfile(), now);
                    _last = _loop.Tick(now);
                }
                if (_last is not null) _history.Record(_last, now);
                DrainEvents();
            }
            SafePollPowerAndLight();
            SafeTickBattery();
            _lighting?.Reapply();

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            int ticks = 0;
            while (timer.WaitForNextTickAsync(stop).AsTask().GetAwaiter().GetResult())
            {
                if (_loop is null) continue;
                var now = DateTimeOffset.Now;
                bool asleep;
                FanStatus? snapStatus = null;
                lock (_gate)
                {
                    asleep = _asleep;
                    if (!asleep)
                    {
                        _fake?.Advance(TimeSpan.FromSeconds(1));
                        _last = snapStatus = _loop.Tick(now);
                    }
                }
                if (asleep) continue;
                if (snapStatus is not null) _history.Record(snapStatus, now);
                DrainEvents();
                ticks++;
                if (ticks % 2 == 0) SafePollPowerAndLight();
                if (ticks % 5 == 0) SafeTickBattery();
                if (ticks % 60 == 0 && _last?.Snapshot is { } s)
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

    private void SafePollPowerAndLight()
    {
        if (_resumeAt is { } at && DateTimeOffset.Now >= at)
        {
            _resumeAt = null;
            try { _power?.Reapply(); } catch (Exception ex) { Log($"power reapply failed: {ex.Message}"); }
            try { _lighting?.Reapply(); } catch (Exception ex) { Log($"lighting reapply failed: {ex.Message}"); }
        }
        if (_power is not null)
        {
            try { _power.Poll(); }
            catch (Exception ex) { Log($"power poll failed: {ex.Message}"); }
        }
        if (_smi is not null && _lighting is not null && _settings.Current.Lighting is not null)
        {
            try
            {
                int level = _smi.ReadLightKeyBrightness();
                if (_lastLightKeyLevel is { } prev && level != prev) _lighting.OnKey(level);
                _lastLightKeyLevel = level;
            }
            catch (Exception) { }
        }
    }

    private void SafeTickBattery()
    {
        try
        {
            var now = DateTimeOffset.Now;
            if (now >= _healthAt)
                _healthAt = now + (_battery.ReadHealth() ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(10));
            _batteryRecorder.Interval = TimeSpan.FromMinutes(_settings.Current.BatteryReadMinutes);
            var pw = _power?.Status;
            string profile = _loop?.Status.Profile ?? _settings.Current.Profile;
            _batteryRecorder.Tick(now, _battery.Live(), pw?.Mode, profile, ecBusy: false, pw?.Cpu?.Watts);
        }
        catch (Exception ex)
        {
            Log($"battery tick failed: {ex.Message}");
        }
    }

    private void OnBatteryIntervalRecorded(BatteryInterval i)
    {
        _batteryCsv.Append(i);
        _batteryEstimates.Add(i);
        Log($"battery: {i.Average.Watts:F1} W over {i.Average.Span.TotalMinutes:F0} min ({i.Mode}, {i.Profile}, brightness {i.Brightness?.ToString() ?? "-"}, CPU {i.CpuWatts?.ToString("F1") ?? "-"} W)");
    }

    private void HandBack()
    {
        try { _history.Dispose(); } catch (Exception) { }
        try { _power?.RestoreCpuLimit(); } catch (Exception ex) { Log($"power restore failed: {ex.Message}"); }
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
            _ = Task.Run(() => Serve(client, stop));
        }
    }

    /// <summary>As the Windows pipe: one JSON line in, one out, any number of exchanges per connection.</summary>
    private void Serve(Socket client, CancellationToken stop)
    {
        try
        {
            using (client)
            using (var stream = new NetworkStream(client))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
            {
                while (!stop.IsCancellationRequested && reader.ReadLine() is { } line)
                    writer.WriteLine(JsonSerializer.Serialize(Handle(line), FanPipe.Json));
            }
        }
        catch (IOException) { } // the client went away
    }

    private FanResponse Handle(string line)
    {
        FanRequest? req = null;
        try { req = JsonSerializer.Deserialize<FanRequest>(line, FanPipe.Json); }
        catch (JsonException) { }
        var now = DateTimeOffset.Now;
        switch (req?.Cmd?.ToLowerInvariant())
        {
            // systemd's sleep hook (excalibur-reimagined sleep pre/post).
            case "sleep" when _loop is not null:
                _resumeAt = null;
                lock (_gate) { _asleep = true; _loop.Stop(now); }
                Log("suspending: fans handed to the EC");
                return new FanResponse(true);
            case "wake" when _loop is not null:
                lock (_gate) { _asleep = false; _loop.Start(SavedProfile(), now); }
                _resumeAt = now + ResumeDelay;
                Log("resumed: fan control restarted");
                return new FanResponse(true);
            // --simulate only: pretend the charger was unplugged / plugged in.
            case "simpowerbattery" or "simpowerac" when _simPower is not null:
                bool onBattery = req!.Cmd.Equals("simPowerBattery", StringComparison.OrdinalIgnoreCase);
                _simPower.OnBattery = onBattery;
                if (_simBattery is not null) _simBattery.OnBattery = onBattery;
                _power?.Poll();
                SafeTickBattery();
                break;
        }
        // Without the EC (Secure Boot, no ec_sys) there is no fan control to talk to; the reason goes to the window.
        if (_commands is null) return new FanResponse(false, _problem ?? "EC is not available.", Code: "ec_unavailable");
        var response = _commands.Handle(line);
        DrainEvents();
        return response;
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
