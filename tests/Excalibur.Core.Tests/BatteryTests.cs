using Excalibur.Core.Battery;
using Excalibur.Core.Fan.Ipc;

namespace Excalibur.Core.Tests;

public class BatteryTests
{
    [Fact]
    public void Health_percent_is_full_over_design_and_never_above_100()
    {
        Assert.Equal(100, new BatteryHealth(61500, 62800, "LION", null, default).Percent);
        Assert.Equal(80, new BatteryHealth(60000, 48000, "LION", null, default).Percent);
        Assert.Null(new BatteryHealth(0, 48000, "LION", null, default).Percent);
    }

    [Fact]
    public void Chemistry_is_four_characters_first_in_the_low_byte()
    {
        Assert.Equal("LION", BatteryChemistry.Decode(BitConverter.ToUInt32("LION"u8)));
        Assert.Equal("LiP", BatteryChemistry.Decode(BitConverter.ToUInt32("LiP\0"u8)));
        Assert.Null(BatteryChemistry.Decode(0));
        Assert.Null(BatteryChemistry.Decode(0x01020304));
    }

    [Theory]
    [InlineData(0, 0, 64, 11520, BatteryState.OnBattery, 192)]
    [InlineData(0, 0, 64, -1, BatteryState.OnBattery, null)]
    [InlineData(1, 8, 64, -1, BatteryState.Charging, null)]
    [InlineData(1, 0, 100, -1, BatteryState.Full, null)]
    [InlineData(1, 0, 80, -1, BatteryState.NotCharging, null)]
    public void Live_state_follows_windows(byte ac, byte flag, byte percent, int life, BatteryState state, int? minutes)
    {
        var live = BatteryLive.From(ac, flag, percent, life);
        Assert.Equal(new BatteryLive(percent, state, minutes), live);
    }

    [Fact]
    public void No_battery_is_null()
    {
        Assert.Null(BatteryLive.From(1, 128, 255, -1));
        Assert.Null(BatteryLive.From(1, 255, 255, -1));
        Assert.Null(BatteryStatusDto.From(null, null));
    }

    [Fact]
    public void A_failed_read_keeps_the_last_health()
    {
        var hw = new FakeBattery { Health = new BatteryHealth(61500, 60000, "LION", 12, DateTimeOffset.Now) };
        var log = new List<string>();
        var monitor = new BatteryMonitor(hw, log.Add);
        Assert.True(monitor.ReadHealth());
        hw.Fail = true;
        Assert.False(monitor.ReadHealth());
        Assert.Equal(60000, monitor.Health?.FullMWh);
        Assert.Contains(log, l => l.Contains("read failed"));

        var dto = BatteryStatusDto.From(new BatteryLive(50, BatteryState.OnBattery, 90), monitor.Health)!;
        Assert.Equal((98, 12, 90), (dto.HealthPercent!.Value, dto.Cycles!.Value, dto.RemainingMinutes!.Value));
    }

    private sealed class FakeBattery : IBatteryHardware
    {
        public BatteryHealth? Health { get; set; }
        public bool Fail { get; set; }

        public BatteryHealth? ReadHealth() => Fail ? throw new System.Management.ManagementException("Generic failure") : Health;

        public BatteryLive? ReadLive() => null;
    }
}
