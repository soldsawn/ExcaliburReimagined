using Excalibur.Core.Power;

namespace Excalibur.Core.Fan;

/// <summary>
/// The CPU package temperature from the CPU's own thermal MSRs, which it updates continuously; never the EC.
/// The firmware's WMI sensor block only refreshes the CPU temperature about every 16.5 s (history of
/// 2026-10-20..22), so a load that stops shows its old temperature for that long.
/// 0x1A2 bits 16-23 = TjMax; 0x1B1 bits 16-22 = degrees below TjMax, bit 31 = the reading is valid.
/// </summary>
public sealed class MsrCpuTemperature(ICpuPowerRegisters registers)
{
    private const uint TemperatureTargetMsr = 0x1A2, PackageThermStatusMsr = 0x1B1;

    private int? _tjMax;

    /// <summary>Degrees C, or null when the MSRs cannot be read or hold no valid reading.</summary>
    public int? Read()
    {
        try
        {
            _tjMax ??= TjMax(registers.ReadMsr(TemperatureTargetMsr));
            return _tjMax is { } tj ? Decode(tj, registers.ReadMsr(PackageThermStatusMsr)) : null;
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>TjMax from MSR 0x1A2; null when it is implausible.</summary>
    public static int? TjMax(ulong temperatureTarget)
    {
        int tj = (int)((temperatureTarget >> 16) & 0xFF);
        return tj is >= 80 and <= 125 ? tj : null;
    }

    /// <summary>The package temperature from MSR 0x1B1; null when its valid bit is clear.</summary>
    public static int? Decode(int tjMax, ulong thermStatus)
        => ((thermStatus >> 31) & 1) != 0 ? tjMax - (int)((thermStatus >> 16) & 0x7F) : null;
}
