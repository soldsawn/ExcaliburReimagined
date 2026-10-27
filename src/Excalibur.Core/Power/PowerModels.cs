using System.Text.Json.Serialization;

namespace Excalibur.Core.Power;

/// <summary>
/// The laptop's performance mode (EC command 0x0300, docs/02): the value is what the firmware
/// takes. It mainly sets the GPU power limit (measured: 100 / 85 / 75 W, docs/03).
/// </summary>
public enum PowerMode
{
    HighPerformance = 0,
    Gaming = 1,
    Office = 2,
}

public static class PowerModeInfo
{
    /// <summary>GPU power limit the firmware sets for each mode, measured with NVML/nvidia-smi (the G870's; see <see cref="PowerModeWatts"/>).</summary>
    public static int GpuWatts(PowerMode mode) => mode switch
    {
        PowerMode.HighPerformance => 100,
        PowerMode.Gaming => 85,
        _ => 75,
    };

    /// <summary>CPU long-term limit (PL1) the firmware sets for each mode in the MCHBAR copy, read live on the G870 (2026-10-22).</summary>
    public static int CpuWatts(PowerMode mode) => mode switch
    {
        PowerMode.HighPerformance => 55,
        PowerMode.Gaming => 45,
        _ => 35,
    };

    /// <summary>
    /// Gaming and High performance need the wall adapter: the stock software forces Office on
    /// battery and with an underpowered USB-C adapter, and so do we (docs/09 A3).
    /// </summary>
    public static bool IsAllowed(PowerMode mode, PowerSource source) => mode == PowerMode.Office || source.CanRunFast;
}

/// <summary>Where the laptop gets its power right now.</summary>
public readonly record struct PowerSource(bool OnBattery, int? BatteryPercent, bool UsbCAdapter)
{
    public bool CanRunFast => !OnBattery && !UsbCAdapter;
}

/// <summary>Why a performance mode cannot be chosen right now; null when it can.</summary>
public enum PowerLock
{
    None = 0,
    OnBattery = 1,
    UsbCAdapter = 2,
}

/// <summary>What to do when the laptop switches to a power source: both parts optional.</summary>
public sealed record PowerRule
{
    /// <summary>The performance mode to switch to; null leaves it alone.</summary>
    public PowerMode? Mode { get; init; }

    /// <summary>A built-in fan profile to switch to; null leaves it alone.</summary>
    public string? FanProfile { get; init; }
}

/// <summary>
/// Automatic changes when the charger is plugged in or out (docs/08 1.4). Off by default, so
/// the app never changes anything the user did not ask for.
/// </summary>
public sealed record PowerAutomation
{
    public bool Enabled { get; init; }

    /// <summary>Applied when running from the wall adapter.</summary>
    public PowerRule OnAc { get; init; } = new();

    /// <summary>Applied on battery or with a weak USB-C adapter; the mode is always Office there.</summary>
    public PowerRule OnBattery { get; init; } = new() { Mode = PowerMode.Office };

    [JsonIgnore]
    public bool IsDefault => this == new PowerAutomation();

    public PowerRule RuleFor(PowerSource source) => source.CanRunFast ? OnAc : OnBattery;
}
