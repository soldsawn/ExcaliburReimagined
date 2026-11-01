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

/// <summary>A request on the control socket: one JSON line.</summary>
internal sealed record LinuxRequest(
    string Cmd,
    string? Profile = null,
    PowerMode? PowerMode = null,
    CpuLimit? CpuLimit = null,
    int? GpuLimitWatts = null,
    CustomProfile? CustomProfile = null,
    PowerAutomation? Automation = null,
    KeyboardLighting? Lighting = null,
    HistoryMode? HistoryMode = null,
    int? BatteryReadMinutes = null,
    int? CpuPercent = null,
    int? GpuPercent = null,
    int? Minutes = null,
    bool? OnBattery = null);

internal sealed record LinuxProfileDto(string Name, bool BuiltIn, IReadOnlyList<CurvePoint>? Cpu, IReadOnlyList<CurvePoint>? Gpu);

internal sealed record LinuxPowerDto(
    PowerMode? Mode,
    PowerMode? Preferred,
    bool OnBattery,
    int? BatteryPercent,
    PowerLock Lock,
    int? CpuWatts,
    int? CpuPl1Watts,
    int? CpuPl2Watts,
    CpuLimit? CpuLimit,
    int? GpuPowerWatts,
    int? GpuTgpWatts,
    int? GpuLimitWatts,
    int? GpuLockMhz,
    PowerAutomation Automation);

internal sealed record LinuxBatteryDto(
    BatteryLive? Live,
    double? WattsNow,
    BatteryHealth? Health,
    BatteryAverage? LastInterval,
    int ReadMinutes,
    IReadOnlyList<BatteryEstimate> Estimates,
    IReadOnlyList<BatteryEstimate> IdleEstimates);

internal sealed record LinuxHistoryDto(
    HistoryMode Mode,
    int RangeMinutes,
    HistorySummary? Summary);

/// <summary>The answer: the service's state as the command line prints it.</summary>
internal sealed record LinuxStatus(
    bool Ok, string? Error, string Mode, string Profile, string? EcVersion, bool Simulated, string? Problem,
    int? CpuTempC, int? GpuTempC, int? CpuRpm, int? GpuRpm, int? CpuPercent, int? GpuPercent, bool HoldsFans,
    string? Trial, IReadOnlyList<string> Events,
    LinuxPowerDto? Power = null,
    KeyboardLighting? Lighting = null,
    IReadOnlyList<LinuxProfileDto>? Profiles = null,
    LinuxBatteryDto? Battery = null,
    LinuxHistoryDto? History = null);

/// <summary>
/// The fan, power, lighting, battery and history service on Linux (L1 + L2): one tick a second of the same
/// <see cref="FanControlLoop"/> as on Windows (curves, safety floors, hot rule, EC version list and trial), plus
/// <see cref="PowerManager"/> every 2 s, <see cref="LightingManager"/> (EC LED registers), <see cref="HistoryRecorder"/> and
/// <see cref="BatteryMonitor"/>/<see cref="BatteryRecorder"/>. The fans are handed to the EC and CPU/GPU limits restored on
/// SIGTERM, on sleep (systemd's sleep hook) and when anything unexpected happens.
/// </summary>
internal sealed class Daemon
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

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
        string cmd = req.Cmd.ToLowerInvariant();
        if (cmd == "guistatus")
        {
            if (_battery.Health is null) _battery.ReadHealth();
            return Status(null, includeProfiles: true, includeBattery: true, historyMinutes: Math.Clamp(req.Minutes ?? 60, 1, 43200));
        }
        if (cmd == "battery")
        {
            if (_battery.Health is null) _battery.ReadHealth();
            return Status(null, includeBattery: true);
        }
        if (cmd == "setbatteryinterval")
        {
            if (req.BatteryReadMinutes is not { } mins) return Status("pil okuma aralığı gerekli", includeBattery: true);
            var err = _settings.Update(s => s with { BatteryReadMinutes = mins }, out _);
            if (err.Count > 0) return Status(string.Join("; ", err), includeBattery: true);
            _batteryRecorder.Interval = TimeSpan.FromMinutes(mins);
            return Status(null, includeBattery: true);
        }
        if (cmd == "history")
            return Status(null, historyMinutes: Math.Clamp(req.Minutes ?? 60, 1, 43200));
        if (cmd == "sethistorymode")
        {
            if (req.HistoryMode is not { } hm || !Enum.IsDefined(hm)) return Status("geçersiz geçmiş modu", historyMinutes: 60);
            var err = _settings.Update(s => s with { History = hm }, out _);
            if (err.Count > 0) return Status(string.Join("; ", err), historyMinutes: 60);
            _history.Mode = hm;
            Log($"history mode set to {hm}");
            return Status(null, historyMinutes: 60);
        }

        if (_loop is null) return Status(cmd == "status" ? null : _problem);
        switch (cmd)
        {
            case "status":
                return Status(null);

            case "setprofile":
                if (req.Profile is null || _settings.Current.FindProfile(req.Profile) is not { } profile)
                    return Status($"bilinmeyen profil: {req.Profile}");
                lock (_gate) _switcher!.Switch(profile, now, "cli");
                DrainEvents();
                return Status(null);

            case "setmanual":
                if (req.CpuPercent is not { } cpu || req.GpuPercent is not { } gpu || req.Minutes is not { } minutes)
                    return Status("manuel hız için yüzdeler ve dakika gerekli");
                try
                {
                    lock (_gate) _loop.SetManual(cpu, gpu, TimeSpan.FromMinutes(minutes), now, "cli");
                    DrainEvents();
                    return Status(null);
                }
                catch (ArgumentException ex) { return Status(ex.Message); }

            case "profiles":
                return Status(null, includeProfiles: true);

            case "saveprofile":
                return SaveProfile(req.CustomProfile, now);

            case "deleteprofile":
                return DeleteProfile(req.Profile, now);

            case "setpowermode":
                if (_power is null || req.PowerMode is not { } mode || !Enum.IsDefined(mode))
                    return Status("geçersiz güç modu");
                var modeRes = _power.SetMode(mode);
                try { _power.Poll(); } catch { }
                return Status(modeRes.Ok ? null : TranslatePowerError(modeRes));

            case "setcpulimit":
                if (_power is null) return Status("güç yönetimi kullanılamıyor");
                var cpuRes = _power.SetCpuLimit(req.CpuLimit);
                try { _power.Poll(); } catch { }
                return Status(cpuRes.Ok ? null : TranslatePowerError(cpuRes));

            case "setgpulimit":
                if (_power is null) return Status("güç yönetimi kullanılamıyor");
                var gpuRes = _power.SetGpuLimit(req.GpuLimitWatts);
                try { _power.Poll(); } catch { }
                return Status(gpuRes.Ok ? null : TranslatePowerError(gpuRes));

            case "setautomation":
                if (req.Automation is null) return Status("otomasyon ayarı eksik");
                var autoErrors = _settings.Update(s => s with { PowerAutomation = req.Automation }, out _);
                if (autoErrors.Count > 0) return Status(string.Join("; ", autoErrors));
                Log($"power automation updated: enabled={req.Automation.Enabled}, ac={req.Automation.OnAc.Mode?.ToString() ?? "-"}/{req.Automation.OnAc.FanProfile ?? "-"}, bat={req.Automation.OnBattery.Mode?.ToString() ?? "-"}/{req.Automation.OnBattery.FanProfile ?? "-"}");
                return Status(null);

            case "setlighting":
                if (_lighting is null || req.Lighting is null) return Status("klavye ışığı ayarı eksik");
                var lightErrors = _lighting.Set(req.Lighting);
                return Status(lightErrors.Count > 0 ? string.Join("; ", lightErrors) : null);

            case "simpower":
                if (!_simulate || _simPower is null || req.OnBattery is not { } onBat)
                    return Status("sim-power yalnız --simulate modunda çalışır");
                _simPower.OnBattery = onBat;
                if (_simBattery is not null) _simBattery.OnBattery = onBat;
                _power?.Poll();
                SafeTickBattery();
                DrainEvents();
                return Status(null);

            case "trial":
                lock (_gate) _loop.StartEcTrial(now);
                DrainEvents();
                return Status(null);

            case "sleep":
                _resumeAt = null;
                lock (_gate) { _asleep = true; _loop.Stop(now); }
                Log("suspending: fans handed to the EC");
                return Status(null);

            case "wake":
                lock (_gate) { _asleep = false; _loop.Start(SavedProfile(), now); }
                _resumeAt = now + ResumeDelay;
                Log("resumed: fan control restarted");
                return Status(null);

            default:
                return Status($"bilinmeyen komut: {req.Cmd}");
        }
    }

    private LinuxStatus SaveProfile(CustomProfile? definition, DateTimeOffset now)
    {
        if (definition is null) return Status("profil tanımı eksik", includeProfiles: true);
        var problems = definition.Validate();
        if (problems.Count > 0) return Status(string.Join("; ", problems), includeProfiles: true);
        var errors = _settings.Update(s => ProfileLibrary.Save(s, definition), out _);
        if (errors.Count > 0) return Status(string.Join("; ", errors), includeProfiles: true);
        string name = definition.Name.Trim();
        Log($"custom profile saved: {name}");
        lock (_gate)
        {
            if (_loop is not null && string.Equals(_loop.Status.Profile, name, StringComparison.OrdinalIgnoreCase)
                && _settings.Current.FindProfile(name) is { } edited)
                _loop.SetProfile(edited, now, FanSource.Service);
        }
        DrainEvents();
        return Status(null, includeProfiles: true);
    }

    private LinuxStatus DeleteProfile(string? name, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name)) return Status("silinecek profil adı gerekli", includeProfiles: true);
        if (FanProfile.FindBuiltIn(name) is not null) return Status($"'{name}' yerleşik bir profil, silinemez", includeProfiles: true);
        if (!_settings.Current.CustomProfiles.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            return Status($"'{name}' adında özel bir profil yok", includeProfiles: true);
        lock (_gate)
        {
            if (_loop is not null && string.Equals(_loop.Status.Profile, name, StringComparison.OrdinalIgnoreCase))
                _switcher!.Switch(FanProfile.Balanced, now, FanSource.Service);
        }
        var errors = _settings.Update(s => ProfileLibrary.Delete(s, name), out _);
        if (errors.Count > 0) return Status(string.Join("; ", errors), includeProfiles: true);
        Log($"custom profile deleted: {name}");
        DrainEvents();
        return Status(null, includeProfiles: true);
    }

    private static string TranslatePowerError(PowerResult res) => res.Code switch
    {
        "power_battery" => "Pilde yalnız Ofis modu kullanılabilir; Oyun ve Yüksek performans için şarj aletini takın.",
        "power_usbc" => "Bu USB-C adaptörü hızlı modlar için zayıf; kutudan çıkan adaptörü kullanın.",
        "cpu_limit_invalid" => $"Geçersiz işlemci sınırı: {res.Message} (uzun süreli 15–55 W, kısa süreli 30–115 W, 5'in katı ve kısa ≥ uzun olmalı).",
        "cpu_limit_failed" => "İşlemci güç sınırı yazılamadı (intel-rapl bulunamadı veya yetki yok).",
        "gpu_limit_invalid" => $"Geçersiz ekran kartı sınırı: {res.Message} (30–175 W, 5'in katı olmalı).",
        "gpu_limit_failed" => "Ekran kartı saat kilidi uygulanamadı (NVIDIA sürücüsü/NVML yanıt vermedi).",
        _ => res.Message ?? "Güç ayarı uygulanamadı.",
    };

    private LinuxStatus Status(string? error, bool includeProfiles = false, bool includeBattery = false, int? historyMinutes = null)
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
        LinuxPowerDto? powerDto = null;
        if (_power?.Status is { } ps)
        {
            powerDto = new LinuxPowerDto(
                ps.Mode, ps.Preferred, ps.Source.OnBattery, ps.Source.BatteryPercent, ps.Lock,
                ps.Cpu?.Watts, ps.Cpu?.Pl1Watts, ps.Cpu?.Pl2Watts, ps.CpuLimit,
                ps.GpuPowerWatts, ps.GpuWatts, ps.GpuLimitWatts, ps.GpuLockMhz,
                _settings.Current.PowerAutomation);
        }
        IReadOnlyList<LinuxProfileDto>? profiles = null;
        if (includeProfiles)
        {
            var list = new List<LinuxProfileDto>();
            foreach (var p in FanProfile.BuiltIn)
                list.Add(p.IsEcAuto ? new(p.Name, true, null, null) : new(p.Name, true, p.CpuCurve.Points, p.GpuCurve.Points));
            list.AddRange(_settings.Current.CustomProfiles.Select(c => new LinuxProfileDto(c.Name, false, c.Cpu, c.Gpu ?? c.Cpu)));
            profiles = list;
        }
        LinuxBatteryDto? batDto = null;
        if (includeBattery)
        {
            double? wattsNow = _simulate
                ? (_simBattery?.OnBattery == true ? 14.0 : null)
                : _linuxBattery?.ReadWattsNow();
            batDto = new LinuxBatteryDto(
                _battery.Live(), wattsNow, _battery.Health, _batteryRecorder.Last,
                _settings.Current.BatteryReadMinutes, _batteryEstimates.All, _batteryEstimates.Idle);
        }
        LinuxHistoryDto? histDto = null;
        if (historyMinutes is { } hm)
        {
            var toUtc = DateTime.UtcNow.AddSeconds(1);
            var fromUtc = toUtc.AddMinutes(-hm);
            var series = _history.Query(fromUtc, toUtc, 600);
            histDto = new LinuxHistoryDto(_history.Mode, hm, HistorySummary.Of(series));
        }
        return new LinuxStatus(error is null, error, s?.Mode.ToString() ?? (_loop is null ? "Unavailable" : "Starting"), s?.Profile ?? _settings.Current.Profile,
            s?.EcVersion, _simulate, _problem, snap?.CpuTempC, snap?.GpuTempC, snap?.CpuRpm, snap?.GpuRpm,
            snap?.CpuAppliedPercent, snap?.GpuAppliedPercent, s?.HoldsFans ?? false,
            s?.Trial is { } t ? $"{t.State}{(t.Problem is null ? "" : $" ({t.Problem})")}" : null, events,
            powerDto, _settings.Current.Lighting, profiles, batDto, histDto);
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
