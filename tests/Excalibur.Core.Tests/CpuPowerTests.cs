using Excalibur.Core.Power;
using Xunit;

namespace Excalibur.Core.Tests;

public class CpuPowerTests
{
    // Read live on the G870 (i5-12450H), 2026-10-22: units 0.125 W / 61 µJ, MSR PL1 65 W / PL2 115 W,
    // MCHBAR PL1 45 W in Gaming mode.
    private const ulong Units = 0x00000000000A0E03, Msr = 0x0042839800DF8208, MchbarGaming = 0x0042839800DF8168;

    private sealed class FakeRegisters : ICpuPowerRegisters
    {
        public uint Energy;
        public ulong? Mchbar = MchbarGaming;
        public ulong ReadMsr(uint index) => index switch
        {
            0x606 => Units,
            0x610 => Msr,
            0x611 => Energy,
            _ => throw new InvalidOperationException(),
        };
        public ulong? ReadMchbarLimit() => Mchbar;
    }

    [Fact]
    public void The_lower_copy_of_each_limit_is_in_force()
    {
        var meter = new CpuPowerMeter(new FakeRegisters());
        var p = meter.Read(TimeSpan.Zero);
        Assert.Equal(45, p.Pl1Watts);
        Assert.Equal(115, p.Pl2Watts);
    }

    [Fact]
    public void Without_the_mchbar_copy_the_msr_limits_count()
    {
        var p = new CpuPowerMeter(new FakeRegisters { Mchbar = null }).Read(TimeSpan.Zero);
        Assert.Equal(65, p.Pl1Watts);
    }

    [Fact]
    public void Power_needs_two_readings_and_survives_the_counter_wrapping()
    {
        var regs = new FakeRegisters { Energy = uint.MaxValue - 81919 }; // 2^14 units = 1 J
        var meter = new CpuPowerMeter(regs);
        Assert.Null(meter.Read(TimeSpan.Zero).Watts);
        regs.Energy = unchecked(uint.MaxValue - 81919 + 20u * 16384); // past the wrap: 20 J in 2 s = 10 W
        Assert.Equal(10, meter.Read(TimeSpan.FromSeconds(2)).Watts);
    }
}
