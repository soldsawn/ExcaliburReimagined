using Excalibur.Core.Fan;
using Excalibur.Core.Power;
using Xunit;

namespace Excalibur.Core.Tests;

public class MsrCpuTemperatureTests
{
    private sealed class Registers(ulong? target, ulong status) : ICpuPowerRegisters
    {
        public int Reads;
        public ulong ReadMsr(uint index)
        {
            Reads++;
            return index switch
            {
                0x1A2 when target is { } t => t,
                0x1B1 => status,
                _ => throw new InvalidOperationException(),
            };
        }
        public void WriteMsr(uint index, ulong value) => throw new InvalidOperationException();
        public ulong? ReadMchbarLimit() => null;
    }

    // TjMax 100 in bits 16-23; valid bit 31 with 42 degrees below TjMax in bits 16-22.
    private const ulong Target100 = 100UL << 16;
    private static ulong Status(int below, bool valid = true) => (valid ? 1UL << 31 : 0) | ((ulong)below << 16) | 0x3;

    [Fact]
    public void Reads_tjmax_minus_the_offset()
        => Assert.Equal(58, new MsrCpuTemperature(new Registers(Target100, Status(42))).Read());

    [Fact]
    public void An_invalid_reading_is_null()
        => Assert.Null(new MsrCpuTemperature(new Registers(Target100, Status(42, valid: false))).Read());

    [Fact]
    public void Unreadable_msrs_are_null()
        => Assert.Null(new MsrCpuTemperature(new Registers(null, Status(42))).Read());

    [Fact]
    public void Implausible_tjmax_is_null()
        => Assert.Null(new MsrCpuTemperature(new Registers(20UL << 16, Status(5))).Read());

    [Fact]
    public void Tjmax_is_read_once()
    {
        var regs = new Registers(Target100, Status(30));
        var sensor = new MsrCpuTemperature(regs);
        sensor.Read();
        sensor.Read();
        Assert.Equal(3, regs.Reads);
    }
}
