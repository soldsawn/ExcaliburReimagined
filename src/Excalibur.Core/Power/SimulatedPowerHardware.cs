namespace Excalibur.Core.Power;

/// <summary>Power without any firmware, for the --simulate service: on the wall, the GPU limit follows the last mode sent.</summary>
public sealed class SimulatedPowerHardware : IPowerHardware
{
    private PowerMode? _mode;

    /// <summary>Set to true to pretend the charger was unplugged.</summary>
    public bool OnBattery { get; set; }

    public void SetMode(PowerMode mode) => _mode = mode;

    public PowerSource ReadSource() => new(OnBattery, OnBattery ? 64 : 100, false);

    /// <summary>70 W is the driver default before any mode was sent.</summary>
    public int? ReadGpuWatts() => _mode is { } mode ? PowerModeInfo.GpuWatts(mode) : 70;

    private CpuLimit? _cpuLimit;

    /// <summary>The G870's PL1 per mode (35/45/55 W), lowered by the user's limit, and a CPU drawing a little under it.</summary>
    public CpuPower? ReadCpuPower()
    {
        int pl1 = PowerModeInfo.CpuWatts(_mode ?? PowerMode.Gaming);
        return new CpuPower(12 + Random.Shared.Next(0, 8), Math.Min(pl1, _cpuLimit?.Pl1Watts ?? pl1), Math.Min(115, _cpuLimit?.Pl2Watts ?? 115), pl1);
    }

    public int? ReadGpuPowerWatts() => 8 + Random.Shared.Next(0, 6);

    public void ApplyCpuLimit(CpuLimit? limit) => _cpuLimit = limit;
}
