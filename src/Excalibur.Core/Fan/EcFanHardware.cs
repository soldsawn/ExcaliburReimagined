using System.Diagnostics;
using System.Management;
using System.Text;
using Excalibur.Core.Hardware;

namespace Excalibur.Core.Fan;

/// <summary>
/// <see cref="IFanHardware"/> on the real machine: temperatures and RPM from the
/// firmware's WMI sensor block (GET 0x0200) at most every 2 s; the fan targets are the ones we wrote, and the EC is read
/// only when a fan does not follow its target (no periodic read: each fell on an ACPI 15 warning, 2026-10-25/26
/// 13:20:00.294, 13:44:51.278 ...); the applied percent is worked out from the RPM
/// (<see cref="FanRpmScale"/>), so it needs no EC read at all. Every EC transaction can collide
/// with Windows' own EC driver reading the battery from the same controller, and a spoiled
/// battery read shuts the machine down as "critical battery"; WMI goes through Windows'
/// driver and is safe. A target we write is not read back: <see cref="FanFollowCheck"/> checks it by
/// the RPM (only 0xFF, the safe state, keeps a read-back). Targets we write update the cached EC values at once, and the
/// Fn+1 Cooling Boost state also arrives at once through the firmware's <c>GMC_WMIEvent</c>
/// (the watcher lives as long as the process; if it cannot start, a fan that does not follow its target makes us read it).
/// The CPU temperature comes from <c>cpuTemperature</c> when given and readable (the CPU's own sensor, current to the
/// second), else from the WMI block, which refreshes it only about every 16.5 s.
/// </summary>
public sealed class EcFanHardware : IFanHardware
{
    private readonly EmbeddedController ec;
    private readonly GmWmiGateway wmi;
    private readonly Func<int?>? cpuTemperature;
    private const int MaxTempC = 115;
    private const int MaxRpm = 7000;
    private readonly FanRpmScale _scale;
    // Windows' EC driver is busy while Windows has run less than this (the battery health read waits it out).
    public static readonly TimeSpan NoRefreshAfterBoot = TimeSpan.FromMinutes(10);

    private EcBlock? _ec;
    private volatile bool _boost;
    private ManagementEventWatcher? _boostWatcher; // kept so it is plainly alive as long as this object
    private readonly FanFollowCheck _follow = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int[] _lastPercent = new int[2];
    // Targets learnt while handing the fans to the EC at start, so the first block read need not read them again.
    private FanTarget? _knownCpu, _knownGpu;
    private readonly string? _versionCachePath;
    // The last WMI sensor block and when it was read: reused while the EC is kept quiet (EcQuiet), and between reads.
    private (int CpuC, int GpuC, int CpuRpm, int GpuRpm)? _lastWmi;
    private readonly Stopwatch _sinceWmi = new();
    private static readonly TimeSpan MaxReuse = TimeSpan.FromSeconds(15);
    // The block is read at most every 2 s (1.5 so a 2-s timer's jitter never skips a read). Each read is an EC transaction
    // through Windows' driver, and the ACPI 13 warnings under load came right after one stalled for 4 s (2026-10-25
    // 17:44, 19:10, 19:46); the CPU temperature, which the curves and safety rules follow closely, stays live (MSR).
    private static readonly TimeSpan MinWmiInterval = TimeSpan.FromSeconds(1.5);
    private bool _reused;
    private GpuTempSource _gpuSource = GpuTempSource.Ec;

    /// <summary>Where the RPM checks of written targets report a fan that does not follow (the service's log).</summary>
    public Action<string>? Log { get; set; }

    private sealed record EcBlock(FanTarget CpuTarget, FanTarget GpuTarget);

    /// <param name="versionCachePath">Where the EC version is kept with the BIOS version it was read under. Reading it costs
    /// twelve EC transactions at every start and wake, and the start's burst of EC traffic is what Windows answered with
    /// ACPI 15 warnings (2026-10-23 21:09:50, three within the version read); it changes only with a BIOS update.</param>
    /// <param name="scale">RPM per percent of each fan (<see cref="FanRpmScale"/>); the G870's when null.</param>
    public EcFanHardware(EmbeddedController ec, GmWmiGateway wmi, Func<int?>? cpuTemperature = null,
        string? versionCachePath = null, FanRpmScale? scale = null)
    {
        _versionCachePath = versionCachePath;
        _scale = scale ?? new FanRpmScale();
        this.ec = ec;
        this.wmi = wmi;
        this.cpuTemperature = cpuTemperature;
        StartBoostWatcher();
    }

    private void StartBoostWatcher()
    {
        try
        {
            var watcher = new ManagementEventWatcher(new ManagementScope(@"root\wmi"), new WqlEventQuery("SELECT * FROM GMC_WMIEvent"));
            watcher.EventArrived += (_, e) =>
            {
                if (e.NewEvent["EventDetail"] is not byte[] detail) return;
                // The EC raised it, so it is busy right now: let it finish before we read again (5 = the charger).
                if (detail.Length > 1) EcQuiet.Hold(detail[1] == 5 ? EcQuiet.AfterCharger : EcQuiet.AfterEvent, $"firmware event {detail[1]}");
                if (TryParseBoostEvent(detail, out bool on)) _boost = on;
            };
            watcher.Start();
            _boostWatcher = watcher;
        }
        catch (ManagementException) { }
    }

    /// <summary>
    /// Fn+1 event payload: byte 1 = 3 (Cooling Boost), byte 3 = 1 when turned on, 0 when off
    /// (checked live on the G870, 2026-10-20).
    /// </summary>
    public static bool TryParseBoostEvent(byte[] detail, out bool on)
    {
        on = false;
        if (detail.Length < 4 || detail[1] != 3) return false;
        on = detail[3] != 0;
        return true;
    }

    public FanSnapshot Read()
    {
        var (cpuC, gpuC, cpuRpm, gpuRpm) = ReadWmiSensors();

        // Read once, and only what the hand-back at start did not tell us; Cooling Boost comes as an event.
        if (_ec is null)
        {
            try
            {
                _ec = new EcBlock(_knownCpu ?? Target(FanRegisters.CpuTarget), _knownGpu ?? Target(FanRegisters.GpuTarget));
                _knownCpu = _knownGpu = null;
            }
            catch (TimeoutException ex) { throw new FanReadException("EC read timed out.", ex); }
        }
        var e = _ec;
        // Reused RPMs say nothing about a target written since.
        if (!_reused)
        {
            _scale.Observe(FanId.Cpu, e.CpuTarget, cpuRpm, _boost, _clock.Elapsed);
            _scale.Observe(FanId.Gpu, e.GpuTarget, gpuRpm, _boost, _clock.Elapsed);
        }
        int cpuPercent = _scale.PercentOf(FanId.Cpu, cpuRpm), gpuPercent = _scale.PercentOf(FanId.Gpu, gpuRpm);
        (_lastPercent[0], _lastPercent[1]) = (cpuPercent, gpuPercent);
        if (!_reused)
        {
            CheckFollow(FanId.Cpu, cpuPercent);
            CheckFollow(FanId.Gpu, gpuPercent);
        }
        return new FanSnapshot(cpuC, gpuC, cpuRpm, gpuRpm, e.CpuTarget, e.GpuTarget, cpuPercent, gpuPercent, _boost, _gpuSource == GpuTempSource.Cpu);
    }

    /// <summary>Acts on <see cref="FanFollowCheck"/>: one more write, or one EC read to see what the register holds.</summary>
    private void CheckFollow(FanId fan, int percent)
    {
        var now = _clock.Elapsed;
        var follow = _follow.Check(fan, percent, _boost, now, out var target);
        if (follow == FanFollow.None) return;
        byte reg = FanRegisters.TargetOf(fan);
        try
        {
            if (follow == FanFollow.Rewrite)
            {
                ec.WriteByte(reg, target.Value);
                return;
            }
            // Fn+1 pressed before we started (no event seen) runs the fans at full speed whatever the target says.
            _boost = (ec.ReadStable(FanRegisters.CoolingBoost) & 1) != 0;
            if (_boost) return;
            var held = FanTarget.FromRaw(ec.ReadStable(reg));
            Log?.Invoke($"!! {fan} fan runs at {percent}% and does not follow its target {target}; the EC holds {held?.ToString() ?? "an invalid value"}");
            // Let the loop see what is really there, so it writes the target again if needed.
            if (held is { } h && _ec is not null)
                _ec = fan == FanId.Cpu ? _ec with { CpuTarget = h } : _ec with { GpuTarget = h };
        }
        catch (TimeoutException) { } // the next check that the fan does not follow tries again
    }

    /// <summary>Temperatures and applied percents from WMI only: never touches the EC (the guardian's normal read).</summary>
    public FanSensors ReadSensors()
    {
        var (cpuC, gpuC, cpuRpm, gpuRpm) = ReadWmiSensors();
        _scale.Refresh(_clock.Elapsed);
        return new FanSensors(cpuC, gpuC, _scale.PercentOf(FanId.Cpu, cpuRpm), _scale.PercentOf(FanId.Gpu, gpuRpm));
    }

    private (int CpuC, int GpuC, int CpuRpm, int GpuRpm) ReadWmiSensors()
    {
        _reused = _lastWmi is not null && (_sinceWmi.Elapsed < MinWmiInterval || EcQuiet.IsQuiet && _sinceWmi.Elapsed < MaxReuse);
        if (_reused) return LastWmi();
        SmiCommand? reply;
        try { reply = wmi.TryExecute(SmiCommand.Get(0x0200)); }
        catch (Exception ex) { throw new FanReadException("WMI sensor read failed.", ex); }
        // Our other process holds the EC (its own call is stalling): do not pile a second one on top.
        if (reply is not { } sensors)
        {
            if (_lastWmi is null || _sinceWmi.Elapsed >= MaxReuse)
                throw new FanReadException("WMI sensor read skipped: the EC lock is held elsewhere.");
            _reused = true;
            return LastWmi();
        }

        int cpuC = (int)sensors.A2, gpuC = (int)sensors.A3, cpuRpm = (int)sensors.A4, gpuRpm = (int)sensors.A5;
        if (cpuTemperature?.Invoke() is { } msrC and >= 1 and <= MaxTempC) cpuC = msrC;
        var (resolvedGpuC, gpuSource) = ResolveGpuTemperature(gpuC, cpuC, Power.Nvml.TemperatureC);
        if (gpuSource != _gpuSource)
        {
            Log?.Invoke(gpuSource switch
            {
                GpuTempSource.Driver => $"GPU temperature: the EC reports {gpuC}C (no reading), using the NVIDIA driver's",
                GpuTempSource.Cpu => $"GPU temperature: the EC reports {gpuC}C and the NVIDIA driver none (GPU off); the GPU fan follows the CPU",
                _ => "GPU temperature: the EC reports it again",
            });
            _gpuSource = gpuSource;
        }
        gpuC = resolvedGpuC;
        if (cpuC is < 1 or > MaxTempC || gpuC is < 1 or > MaxTempC)
            throw new FanReadException($"Implausible temperatures CPU={cpuC} GPU={gpuC}.");
        if (cpuRpm is < 0 or > MaxRpm || gpuRpm is < 0 or > MaxRpm)
            throw new FanReadException($"Implausible RPM CPU={cpuRpm} GPU={gpuRpm}.");
        _lastWmi = (cpuC, gpuC, cpuRpm, gpuRpm);
        _sinceWmi.Restart();
        return (cpuC, gpuC, cpuRpm, gpuRpm);
    }

    private (int CpuC, int GpuC, int CpuRpm, int GpuRpm) LastWmi()
    {
        var last = _lastWmi!.Value;
        // The CPU temperature does not come from the EC (MSR): it stays live.
        if (cpuTemperature?.Invoke() is { } live and >= 1 and <= MaxTempC) last = last with { CpuC = live };
        return last;
    }

    /// <summary>
    /// What the EC reports when it has no GPU reading. Seen on the G870 while the GPU was switched off (GPU mode trial,
    /// 2026-10-23 20:04-20:30: 30 C throughout, once 0, which faulted the loop); E-Helper's notes say some models report
    /// 30 C all the time.
    /// </summary>
    public const int PlaceholderGpuC = 30;

    /// <summary>
    /// The GPU temperature the fans follow: the EC's, unless it is 0 or <see cref="PlaceholderGpuC"/>; then the NVIDIA
    /// driver's (asked only then, so a sleeping GPU is not woken on a machine whose EC reports it), and when the driver has
    /// none either the GPU is off and its fan follows the CPU (the heat pipes are shared; user's choice, 2026-10-26).
    /// </summary>
    public static (int C, GpuTempSource Source) ResolveGpuTemperature(int ecC, int cpuC, Func<int?> driverC)
    {
        if (ecC is not (0 or PlaceholderGpuC)) return (ecC, GpuTempSource.Ec);
        if (driverC() is { } c and >= 1 and <= MaxTempC) return (c, GpuTempSource.Driver);
        return (cpuC, GpuTempSource.Cpu);
    }

    public void SetTarget(FanId fan, FanTarget target)
    {
        byte reg = FanRegisters.TargetOf(fan);
        if (!FanRegisters.IsWritable(reg, target.Value))
            throw new InvalidOperationException($"Refusing to write 0x{target.Value:X2} to 0x{reg:X2}.");

        // At start (nothing known yet) the fans are usually on EC automatic already: look before writing.
        if (target.IsAuto && _ec is null)
        {
            try
            {
                if (ec.ReadStable(reg) == target.Value)
                {
                    Learnt(fan, target);
                    return;
                }
            }
            catch (EcBusyException ex) { throw new FanBusyException($"{fan} fan target {target} not checked: the EC is busy.", ex); }
            catch (TimeoutException) { }
        }

        // One retry: a write that timed out may or may not have landed, and for 0xFF a stray byte from
        // Windows' EC driver can spoil the single read-back.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                ec.WriteByte(reg, target.Value);
                // Only the safe state is read back; any other target is checked by the RPM.
                if (!target.IsAuto || ec.ReadStable(reg) == target.Value)
                {
                    _follow.Wrote(fan, target, _lastPercent[(int)fan], _clock.Elapsed);
                    if (_ec is not null)
                        _ec = fan == FanId.Cpu ? _ec with { CpuTarget = target } : _ec with { GpuTarget = target };
                    else if (target.IsAuto)
                        Learnt(fan, target);
                    return;
                }
            }
            catch (EcBusyException ex) { throw new FanBusyException($"{fan} fan target {target} not written: the EC is busy.", ex); }
            catch (TimeoutException) { }
        }
        throw new FanWriteException(target.IsAuto
            ? $"{fan} fan target {target} did not read back from 0x{reg:X2}."
            : $"{fan} fan target {target} could not be written to 0x{reg:X2}.");
    }

    /// <summary>
    /// Hands both fans to the EC without reading anything: the start on a verified EC, whose first tick writes the
    /// profile's targets right after (checked by the RPM). The reads this used to make fell on ACPI 15 warnings at
    /// every start (2026-10-25 13:05:14.120, 10-26 13:32:02.229).
    /// </summary>
    public void ResetToAutoAtStart()
    {
        Exception? first = null;
        foreach (var fan in new[] { FanId.Cpu, FanId.Gpu })
        {
            try
            {
                ec.WriteByte(FanRegisters.TargetOf(fan), FanTarget.Auto.Value);
                _follow.Wrote(fan, FanTarget.Auto, _lastPercent[(int)fan], _clock.Elapsed);
                if (_ec is not null)
                    _ec = fan == FanId.Cpu ? _ec with { CpuTarget = FanTarget.Auto } : _ec with { GpuTarget = FanTarget.Auto };
                else
                    Learnt(fan, FanTarget.Auto);
            }
            catch (TimeoutException ex) { first ??= ex; }
        }
        if (first is not null) throw new FanWriteException("Could not hand the fans back to the EC.", first);
    }

    private void Learnt(FanId fan, FanTarget target)
    {
        if (fan == FanId.Cpu) _knownCpu = target;
        else _knownGpu = target;
    }

    public string? ReadEcVersion()
    {
        string? version = ReadEcVersionCached();
        _scale.UseEcVersion(version);
        return version;
    }

    private string? ReadEcVersionCached()
    {
        string? bios = BiosVersion();
        if (_versionCachePath is not null && bios is not null)
        {
            try
            {
                var lines = File.ReadAllLines(_versionCachePath);
                if (lines is [var b, var v] && b == bios && IsEcVersion(v)) return v;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        string? read = ReadEcVersionFromEc();
        if (read is not null && _versionCachePath is not null && bios is not null)
        {
            try { File.WriteAllLines(_versionCachePath, [bios, read]); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return read;
    }

    private static bool IsEcVersion(string v) => v.Length == FanRegisters.EcVersionLength && v.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z');

    /// <summary>The BIOS version and date from Windows (SMBIOS, no EC access); an EC update comes with a BIOS update.</summary>
    private static string? BiosVersion()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            foreach (ManagementObject o in searcher.Get())
                using (o)
                    return $"{o["SMBIOSBIOSVersion"]} {o["ReleaseDate"]}";
        }
        catch (ManagementException) { }
        return null;
    }

    private string? ReadEcVersionFromEc()
    {
        // Read once at start; a busy bus only delays it (the stale-byte drain frees it within seconds).
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                var sb = new StringBuilder();
                for (int i = 0; i < FanRegisters.EcVersionLength; i++)
                    sb.Append((char)ec.ReadStable((byte)(FanRegisters.EcVersionStart + i)));
                string version = sb.ToString();
                return version.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z') ? version : null;
            }
            catch (EcBusyException) { Thread.Sleep(100); }
            catch (TimeoutException) { return null; }
        }
        return null;
    }

    private FanTarget Target(byte reg)
        => FanTarget.FromRaw(ec.ReadStable(reg))
           ?? throw new FanReadException($"Fan target 0x{reg:X2} holds an invalid value.");
}
