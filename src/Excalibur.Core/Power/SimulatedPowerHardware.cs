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
}
