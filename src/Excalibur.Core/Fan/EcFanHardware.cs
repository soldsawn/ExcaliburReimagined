using System.Diagnostics;
using System.Management;
using System.Text;
using Excalibur.Core.Hardware;

namespace Excalibur.Core.Fan;

/// <summary>
/// <see cref="IFanHardware"/> on the real machine: temperatures and RPM from the
/// firmware's WMI sensor block (GET 0x0200) on every read, fan targets / Cooling Boost from
/// the EC only every <c>ecRefresh</c>; the applied percent is worked out from the RPM
/// (<see cref="AppliedPercent"/>), so it needs no EC read at all. Every EC transaction can collide
/// with Windows' own EC driver reading the battery from the same controller, and a spoiled
/// battery read shuts the machine down as "critical battery"; WMI goes through Windows'
/// driver and is safe. A target we write is not read back: <see cref="FanFollowCheck"/> checks it by
/// the RPM (only 0xFF, the safe state, keeps a read-back). Targets we write update the cached EC values at once, and the
/// Fn+1 Cooling Boost state also arrives at once through the firmware's <c>GMC_WMIEvent</c>
/// (the watcher lives as long as the process; if it cannot start, the EC refresh still catches the state).
/// </summary>
public sealed class EcFanHardware : IFanHardware
{
    private readonly EmbeddedController ec;
    private readonly GmWmiGateway wmi;
    private const int MaxTempC = 115;
    private const int MaxRpm = 7000;
    // Measured on the G870: the EC drives CPU fan at rpm/54.5 percent and GPU fan at rpm/52.5.
    private const double CpuRpmPerPercent = 54.5, GpuRpmPerPercent = 52.5;
    private static readonly TimeSpan DefaultEcRefresh = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _ecRefresh;
    private readonly Stopwatch _sinceEcRead = new();
    private EcBlock? _ec;
    private volatile bool _boost;
    private ManagementEventWatcher? _boostWatcher; // kept so it is plainly alive as long as this object
    private readonly FanFollowCheck _follow = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int[] _lastPercent = new int[2];

    /// <summary>Where the RPM checks of written targets report a fan that does not follow (the service's log).</summary>
    public Action<string>? Log { get; set; }

    private sealed record EcBlock(FanTarget CpuTarget, FanTarget GpuTarget);

    public EcFanHardware(EmbeddedController ec, GmWmiGateway wmi, TimeSpan? ecRefresh = null)
    {
        this.ec = ec;
        this.wmi = wmi;
        _ecRefresh = ecRefresh ?? DefaultEcRefresh;
        StartBoostWatcher();
    }

    private void StartBoostWatcher()
    {
        try
        {
            var watcher = new ManagementEventWatcher(new ManagementScope(@"root\wmi"), new WqlEventQuery("SELECT * FROM GMC_WMIEvent"));
            watcher.EventArrived += (_, e) =>
            {
                if (e.NewEvent["EventDetail"] is byte[] detail && TryParseBoostEvent(detail, out bool on)) _boost = on;
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

        if (_ec is null || _sinceEcRead.Elapsed >= _ecRefresh)
        {
            try
            {
                _ec = new EcBlock(Target(FanRegisters.CpuTarget), Target(FanRegisters.GpuTarget));
                _boost = (ec.ReadStable(FanRegisters.CoolingBoost) & 1) != 0;
                _sinceEcRead.Restart();
            }
            catch (TimeoutException ex) { throw new FanReadException("EC read timed out.", ex); }
        }
        int cpuPercent = AppliedPercent(cpuRpm, CpuRpmPerPercent), gpuPercent = AppliedPercent(gpuRpm, GpuRpmPerPercent);
        (_lastPercent[0], _lastPercent[1]) = (cpuPercent, gpuPercent);
        CheckFollow(FanId.Cpu, cpuPercent);
        CheckFollow(FanId.Gpu, gpuPercent);
        var e = _ec;
        return new FanSnapshot(cpuC, gpuC, cpuRpm, gpuRpm, e.CpuTarget, e.GpuTarget, cpuPercent, gpuPercent, _boost);
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
            var held = FanTarget.FromRaw(ec.ReadStable(reg));
            Log?.Invoke($"!! {fan} fan runs at {percent}% and does not follow its target {target}; the EC holds {held?.ToString() ?? "an invalid value"}");
            // Let the loop see what is really there, so it writes the target again if needed.
            if (held is { } h && _ec is not null)
                _ec = fan == FanId.Cpu ? _ec with { CpuTarget = h } : _ec with { GpuTarget = h };
        }
        catch (TimeoutException) { } // the 60-s EC refresh shows the truth anyway
    }

    /// <summary>Temperatures and applied percents from WMI only: never touches the EC (the guardian's normal read).</summary>
    public FanSensors ReadSensors()
    {
        var (cpuC, gpuC, cpuRpm, gpuRpm) = ReadWmiSensors();
        return new FanSensors(cpuC, gpuC, AppliedPercent(cpuRpm, CpuRpmPerPercent), AppliedPercent(gpuRpm, GpuRpmPerPercent));
    }

    private (int CpuC, int GpuC, int CpuRpm, int GpuRpm) ReadWmiSensors()
    {
        SmiCommand sensors;
        try { sensors = wmi.Execute(SmiCommand.Get(0x0200)); }
        catch (Exception ex) { throw new FanReadException("WMI sensor read failed.", ex); }

        int cpuC = (int)sensors.A2, gpuC = (int)sensors.A3, cpuRpm = (int)sensors.A4, gpuRpm = (int)sensors.A5;
        if (cpuC is < 1 or > MaxTempC || gpuC is < 1 or > MaxTempC)
            throw new FanReadException($"Implausible temperatures CPU={cpuC} GPU={gpuC}.");
        if (cpuRpm is < 0 or > MaxRpm || gpuRpm is < 0 or > MaxRpm)
            throw new FanReadException($"Implausible RPM CPU={cpuRpm} GPU={gpuRpm}.");
        return (cpuC, gpuC, cpuRpm, gpuRpm);
    }

    public void SetTarget(FanId fan, FanTarget target)
    {
        byte reg = FanRegisters.TargetOf(fan);
        if (!FanRegisters.IsWritable(reg, target.Value))
            throw new InvalidOperationException($"Refusing to write 0x{target.Value:X2} to 0x{reg:X2}.");

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
                    return;
                }
            }
            catch (TimeoutException) { }
        }
        throw new FanWriteException(target.IsAuto
            ? $"{fan} fan target {target} did not read back from 0x{reg:X2}."
            : $"{fan} fan target {target} could not be written to 0x{reg:X2}.");
    }

    public string? ReadEcVersion()
    {
        try
        {
            var sb = new StringBuilder();
            for (int i = 0; i < FanRegisters.EcVersionLength; i++)
                sb.Append((char)ec.ReadStable((byte)(FanRegisters.EcVersionStart + i)));
            string version = sb.ToString();
            return version.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z') ? version : null;
        }
        catch (TimeoutException) { return null; }
    }

    private FanTarget Target(byte reg)
        => FanTarget.FromRaw(ec.ReadStable(reg))
           ?? throw new FanReadException($"Fan target 0x{reg:X2} holds an invalid value.");

    /// <summary>The percent the EC is driving a fan at, from its RPM; clamped to 0..100.</summary>
    public static int AppliedPercent(int rpm, double rpmPerPercent)
        => Math.Clamp((int)Math.Round(rpm / rpmPerPercent), 0, 100);
}
