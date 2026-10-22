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
        public ulong Limit = Msr;
        public ulong? Mchbar = MchbarGaming;
        public List<ulong> Writes { get; } = [];
        public ulong ReadMsr(uint index) => index switch
        {
            0x606 => Units,
            0x610 => Limit,
            0x611 => Energy,
            _ => throw new InvalidOperationException(),
        };
        public void WriteMsr(uint index, ulong value)
        {
            Assert.Equal(0x610u, index);
            Writes.Add(value);
            Limit = value;
        }
        public ulong? ReadMchbarLimit() => Mchbar;
    }

    [Fact]
    public void A_limit_changes_only_the_two_power_fields()
    {
        var regs = new FakeRegisters();
        new CpuLimiter(regs).Apply(new CpuLimit(15, 20));
        // The value msr-test wrote on the G870, 2026-10-22, which held the CPU at 20.0 W.
        Assert.Equal([0x004280A000DF8078UL], regs.Writes);
    }

    [Fact]
    public void Applying_the_same_limit_again_writes_nothing_and_clearing_restores_the_original()
    {
        var regs = new FakeRegisters();
        var limiter = new CpuLimiter(regs);
        limiter.Apply(new CpuLimit(25, 35));
        limiter.Apply(new CpuLimit(25, 35));
        Assert.Single(regs.Writes);
        limiter.Apply(null);
        Assert.Equal(Msr, regs.Limit);
        Assert.Equal(2, regs.Writes.Count);
    }

    [Fact]
    public void A_limit_the_firmware_reset_is_written_again()
    {
        var regs = new FakeRegisters();
        var limiter = new CpuLimiter(regs);
        limiter.Apply(new CpuLimit(25, 35));
        regs.Limit = Msr; // e.g. after sleep
        limiter.Apply(new CpuLimit(25, 35));
        Assert.Equal(2, regs.Writes.Count);
    }

    [Fact]
    public void Clearing_without_ever_limiting_touches_nothing()
    {
        var regs = new FakeRegisters();
        new CpuLimiter(regs).Apply(null);
        Assert.Empty(regs.Writes);
    }

    [Theory]
    [InlineData(25, 35, true)]
    [InlineData(10, 35, false)]
    [InlineData(25, 120, false)]
    [InlineData(27, 35, false)]
    [InlineData(45, 40, false)]
    public void Limits_are_checked(int pl1, int pl2, bool ok) => Assert.Equal(ok, new CpuLimit(pl1, pl2).Validate().Count == 0);

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
