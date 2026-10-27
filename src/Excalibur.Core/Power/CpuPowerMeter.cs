namespace Excalibur.Core.Power;

/// <summary>
/// The CPU package now: the power it draws (null until two readings exist) and the limits in force,
/// in whole watts. A limit is the lower of its MSR and MCHBAR copies: the power modes set PL1 in the
/// MCHBAR copy (Office 35, Gaming 45, High performance 55 W on the G870) and leave the MSR at 65 W.
/// <see cref="ModePl1Watts"/> is the MCHBAR copy alone: the power mode's, without the user's own limit.
/// </summary>
public sealed record CpuPower(int? Watts, int? Pl1Watts, int? Pl2Watts, int? ModePl1Watts = null);

/// <summary>The registers <see cref="CpuPowerMeter"/> reads; the real ones go through PawnIO's IntelMSR and IntelMCHBAR modules.</summary>
public interface ICpuPowerRegisters
{
    /// <summary>Throws <see cref="InvalidOperationException"/> when the read fails.</summary>
    ulong ReadMsr(uint index);

    /// <summary>Only <see cref="CpuLimiter"/> calls this, and only for 0x610. Throws <see cref="InvalidOperationException"/> on failure.</summary>
    void WriteMsr(uint index, ulong value);

    /// <summary>The MCHBAR copy of the package limit (MCHBAR + 0x59A0); null when it cannot be read.</summary>
    ulong? ReadMchbarLimit();
}

/// <summary>Reads the CPU's power from its RAPL energy counter and its limits (all read-only; never the EC).</summary>
public sealed class CpuPowerMeter(ICpuPowerRegisters registers)
{
    private const uint PowerUnitMsr = 0x606, PackageLimitMsr = 0x610, PackageEnergyMsr = 0x611;

    private (double Watt, double Joule)? _units;
    private (uint Energy, TimeSpan At)? _last;

    /// <param name="now">A monotonic clock (e.g. a Stopwatch's elapsed time).</param>
    public CpuPower Read(TimeSpan now)
    {
        var (watt, joule) = _units ??= Units(registers.ReadMsr(PowerUnitMsr));

        uint energy = (uint)registers.ReadMsr(PackageEnergyMsr);
        int? watts = null;
        if (_last is { } last && now > last.At)
            watts = (int)Math.Round(unchecked(energy - last.Energy) * joule / (now - last.At).TotalSeconds);
        _last = (energy, now);

        var (msr1, msr2) = Limits(registers.ReadMsr(PackageLimitMsr), watt);
        var (mmio1, mmio2) = registers.ReadMchbarLimit() is { } mmio ? Limits(mmio, watt) : (null, null);
        return new CpuPower(watts, Lower(msr1, mmio1), Lower(msr2, mmio2), mmio1);
    }

    private static (double Watt, double Joule) Units(ulong raw) =>
        (1.0 / (1 << (int)(raw & 0xF)), 1.0 / (1 << (int)((raw >> 8) & 0x1F)));

    /// <summary>PL1 in bits 0-14 (enable bit 15), PL2 in bits 32-46 (enable bit 47); a disabled limit is null.</summary>
    private static (int? Pl1, int? Pl2) Limits(ulong raw, double watt) =>
        (((raw >> 15) & 1) != 0 ? (int)Math.Round((raw & 0x7FFF) * watt) : null,
         ((raw >> 47) & 1) != 0 ? (int)Math.Round(((raw >> 32) & 0x7FFF) * watt) : null);

    private static int? Lower(int? a, int? b) => a is { } x && b is { } y ? Math.Min(x, y) : a ?? b;
}
