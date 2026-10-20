using System.Diagnostics;
using System.Text;
using Excalibur.Core.Hardware;

namespace Excalibur.Core.Fan;

/// <summary>
/// <see cref="IFanHardware"/> on the real machine: temperatures and RPM from the
/// firmware's WMI sensor block (GET 0x0200) on every read, fan targets / applied percent /
/// Cooling Boost from the EC only every <c>ecRefresh</c>. Every EC transaction can collide
/// with Windows' own EC driver reading the battery from the same controller, and a spoiled
/// battery read shuts the machine down as "critical battery"; WMI goes through Windows'
/// driver and is safe. Targets we write and verify update the cached EC values at once.
/// </summary>
public sealed class EcFanHardware(EmbeddedController ec, GmWmiGateway wmi, TimeSpan? ecRefresh = null) : IFanHardware
{
    private const int MaxTempC = 115;
    private const int MaxRpm = 7000;
    private static readonly TimeSpan DefaultEcRefresh = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _ecRefresh = ecRefresh ?? DefaultEcRefresh;
    private readonly Stopwatch _sinceEcRead = new();
    private EcBlock? _ec;

    private sealed record EcBlock(FanTarget CpuTarget, FanTarget GpuTarget, int CpuApplied, int GpuApplied, bool Boost);

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
                    Percent(FanRegisters.CpuApplied), Percent(FanRegisters.GpuApplied),
                    (ec.ReadStable(FanRegisters.CoolingBoost) & 1) != 0);
                _sinceEcRead.Restart();
            }
            catch (TimeoutException ex) { throw new FanReadException("EC read timed out.", ex); }
        }
        var e = _ec;
        return new FanSnapshot(cpuC, gpuC, cpuRpm, gpuRpm, e.CpuTarget, e.GpuTarget, e.CpuApplied, e.GpuApplied, e.Boost);
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

    private int Percent(byte reg)
    {
        byte v = ec.ReadStable(reg);
        return v <= 100 ? v : throw new FanReadException($"Applied percent 0x{reg:X2} = {v} is out of range.");
    }
}
