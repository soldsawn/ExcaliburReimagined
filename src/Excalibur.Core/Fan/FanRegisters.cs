namespace Excalibur.Core.Fan;

/// <summary>
/// The EC offsets the fan feature uses (ACPI EC space, docs/06-07). Only
/// <see cref="CpuTarget"/> and <see cref="GpuTarget"/> are ever written, and only with a
/// percent 0-100 or 0xFF; <see cref="IsWritable"/> is the single place that rule lives.
/// </summary>
public static class FanRegisters
{
    public const byte CpuTarget = 0x2C;   // CPU fan target %, 0xFF = EC automatic
    public const byte GpuTarget = 0x2D;   // GPU fan target %, 0xFF = EC automatic
    public const byte CpuApplied = 0x2E;  // percent the EC is driving the CPU fan at
    public const byte GpuApplied = 0x2F;  // percent the EC is driving the GPU fan at
    public const byte CoolingBoost = 0xC3; // FANM; bit0 = Fn+1 Cooling Boost
    public const byte EcVersionStart = 0xFA; // 6 ASCII bytes, e.g. "P008B0"
    public const int EcVersionLength = 6;

    public static byte TargetOf(FanId fan) => fan == FanId.Cpu ? CpuTarget : GpuTarget;

    public static byte AppliedOf(FanId fan) => fan == FanId.Cpu ? CpuApplied : GpuApplied;

    public static bool IsWritable(byte address, byte value)
        => address is CpuTarget or GpuTarget && (value <= 100 || value == FanTarget.AutoValue);
}
