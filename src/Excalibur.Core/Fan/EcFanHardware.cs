using System.Diagnostics;
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
/// driver and is safe. Targets we write and verify update the cached EC values at once.
/// </summary>
public sealed class EcFanHardware(EmbeddedController ec, GmWmiGateway wmi, TimeSpan? ecRefresh = null) : IFanHardware
{
    private const int MaxTempC = 115;
    private const int MaxRpm = 7000;
    // Measured on the G870: the EC drives CPU fan at rpm/54.5 percent and GPU fan at rpm/52.5.
    private const double CpuRpmPerPercent = 54.5, GpuRpmPerPercent = 52.5;
    private static readonly TimeSpan DefaultEcRefresh = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _ecRefresh = ecRefresh ?? DefaultEcRefresh;
    private readonly Stopwatch _sinceEcRead = new();
    private EcBlock? _ec;

    private sealed record EcBlock(FanTarget CpuTarget, FanTarget GpuTarget, bool Boost);

    public FanSnapshot Read()
    {
        SmiCommand sensors;
        try { sensors = wmi.Execute(SmiCommand.Get(0x0200)); }
        catch (Exception ex) { throw new FanReadException("WMI sensor read failed.", ex); }

        int cpuC = (int)sensors.A2, gpuC = (int)sensors.A3, cpuRpm = (int)sensors.A4, gpuRpm = (int)sensors.A5;
        if (cpuC is < 1 or > MaxTempC || gpuC is < 1 or > MaxTempC)
            throw new FanReadException($"Implausible temperatures CPU={cpuC} GPU={gpuC}.");
        if (cpuRpm is < 0 or > MaxRpm || gpuRpm is < 0 or > MaxRpm)
            throw new FanReadException($"Implausible RPM CPU={cpuRpm} GPU={gpuRpm}.");

        if (_ec is null || _sinceEcRead.Elapsed >= _ecRefresh)
        {
            try
            {
                _ec = new EcBlock(Target(FanRegisters.CpuTarget), Target(FanRegisters.GpuTarget),
                    (ec.ReadStable(FanRegisters.CoolingBoost) & 1) != 0);
                _sinceEcRead.Restart();
            }
            catch (TimeoutException ex) { throw new FanReadException("EC read timed out.", ex); }
        }
        var e = _ec;
        return new FanSnapshot(cpuC, gpuC, cpuRpm, gpuRpm, e.CpuTarget, e.GpuTarget,
            AppliedPercent(cpuRpm, CpuRpmPerPercent), AppliedPercent(gpuRpm, GpuRpmPerPercent), e.Boost);
    }

    public void SetTarget(FanId fan, FanTarget target)
    {
        byte reg = FanRegisters.TargetOf(fan);
        if (!FanRegisters.IsWritable(reg, target.Value))
            throw new InvalidOperationException($"Refusing to write 0x{target.Value:X2} to 0x{reg:X2}.");

        // One retry: a stray byte from Windows' EC driver can spoil a single read-back.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                ec.WriteByte(reg, target.Value);
                if (ec.ReadStable(reg) == target.Value)
                {
                    if (_ec is not null)
                        _ec = fan == FanId.Cpu ? _ec with { CpuTarget = target } : _ec with { GpuTarget = target };
                    return;
                }
            }
            catch (TimeoutException) { }
        }
        throw new FanWriteException($"{fan} fan target {target} did not read back from 0x{reg:X2}.");
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
