namespace Excalibur.Core.Power;

/// <summary>
/// The user's own CPU power limits, written to the MSR copy (0x610). The CPU obeys the lower of the MSR
/// and MCHBAR copies, so these can only lower what the power mode allows (Office 35, Gaming 45, High
/// performance 55 W long-term on the G870). Not set = the power mode decides and nothing is written.
/// </summary>
public sealed record CpuLimit(int Pl1Watts, int Pl2Watts)
{
    public const int MinPl1 = 15, MaxPl1 = 55, MinPl2 = 30, MaxPl2 = 115, Step = 5;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Pl1Watts is < MinPl1 or > MaxPl1 || Pl1Watts % Step != 0) errors.Add($"long-term limit must be {MinPl1}-{MaxPl1} W in steps of {Step}");
        if (Pl2Watts is < MinPl2 or > MaxPl2 || Pl2Watts % Step != 0) errors.Add($"short-term limit must be {MinPl2}-{MaxPl2} W in steps of {Step}");
        if (Pl2Watts < Pl1Watts) errors.Add("the short-term limit cannot be below the long-term one");
        return errors;
    }
}

/// <summary>
/// Writes <see cref="CpuLimit"/> to MSR 0x610 and puts the original back. Only the two power fields change
/// (enable, clamp and time-window bits keep the firmware's values), and only when the register differs, so
/// calling <see cref="Apply"/> every minute costs one read. The original is captured on the first write;
/// a restart of Windows restores the BIOS values in any case.
/// </summary>
public sealed class CpuLimiter(ICpuPowerRegisters registers)
{
    private const uint PowerUnitMsr = 0x606, PackageLimitMsr = 0x610;
    private const ulong PowerFields = 0x7FFFUL | (0x7FFFUL << 32);

    private ulong? _original;
    private double? _watt;

    /// <summary>Makes the register hold <paramref name="limit"/>, or the original when null.</summary>
    public void Apply(CpuLimit? limit)
    {
        if (limit is null && _original is null) return; // never touched: nothing to undo
        ulong current = registers.ReadMsr(PackageLimitMsr);
        _original ??= current;
        ulong wanted = limit is null ? _original.Value : Compose(_original.Value, limit);
        if (current != wanted) registers.WriteMsr(PackageLimitMsr, wanted);
    }

    private ulong Compose(ulong original, CpuLimit limit)
    {
        double watt = _watt ??= 1.0 / (1 << (int)(registers.ReadMsr(PowerUnitMsr) & 0xF));
        ulong pl1 = (ulong)Math.Round(limit.Pl1Watts / watt) & 0x7FFF, pl2 = (ulong)Math.Round(limit.Pl2Watts / watt) & 0x7FFF;
        return (original & ~PowerFields) | pl1 | (pl2 << 32);
    }
}
