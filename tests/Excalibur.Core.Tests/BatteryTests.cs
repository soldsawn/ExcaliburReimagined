using Excalibur.Core.Battery;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

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

    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(3));
    private static readonly BatteryLive Discharging = new(60, BatteryState.OnBattery, 120);
    private static readonly BatteryLive Charging = new(60, BatteryState.Charging, null);

    /// <summary>Ticks every 5 s from <paramref name="from"/> for <paramref name="span"/>.</summary>
    private static DateTimeOffset Run(BatteryRecorder r, DateTimeOffset from, TimeSpan span, BatteryLive live,
        string profile = "Balanced", bool ecBusy = false)
    {
        var t = from;
        for (; t <= from + span; t += TimeSpan.FromSeconds(5)) r.Tick(t, live, PowerMode.Office, profile, ecBusy);
        return t;
    }

    [Fact]
    public void Average_is_the_energy_drop_over_the_interval_with_two_reads()
    {
        var meter = new FakeMeter(40000);
        var r = new BatteryRecorder(meter);
        var kept = new List<BatteryInterval>();
        r.Recorded += kept.Add;
        meter.Drop = 2500; // per read: 2.5 Wh in 10 min = 15 W
        var t = Run(r, T0, TimeSpan.FromSeconds(55), Discharging);
        Assert.Equal(0, meter.Reads); // the first read waits a minute after the charger comes out
        t = Run(r, t, TimeSpan.FromMinutes(11), Discharging);
        Assert.Equal(2, meter.Reads);
        var i = Assert.Single(kept);
        Assert.Equal(15, i.Average.Watts, 1);
        Assert.Equal(("Balanced", 60), (i.Profile, i.Percent));
        Assert.Same(i.Average, r.Last);
    }

    [Fact]
    public void No_reads_on_the_wall_and_the_average_is_forgotten()
    {
        var meter = new FakeMeter(40000) { Drop = 2500 };
        var r = new BatteryRecorder(meter);
        var t = Run(r, T0, TimeSpan.FromMinutes(12), Discharging);
        Assert.NotNull(r.Last);
        int reads = meter.Reads;
        Run(r, t, TimeSpan.FromHours(1), Charging);
        Assert.Equal(reads, meter.Reads);
        Assert.Null(r.Last);
    }

    [Fact]
    public void A_profile_change_drops_the_interval_without_an_extra_read()
    {
        var meter = new FakeMeter(40000) { Drop = 2500 };
        var r = new BatteryRecorder(meter);
        var kept = new List<BatteryInterval>();
        r.Recorded += kept.Add;
        var t = Run(r, T0, TimeSpan.FromMinutes(5), Discharging);
        Assert.Equal(1, meter.Reads);
        t = Run(r, t, TimeSpan.FromMinutes(6), Discharging, profile: "Quiet");
        Assert.Equal(2, meter.Reads);
        Assert.Empty(kept);
        // The next interval runs on Quiet throughout and is kept.
        Run(r, t, TimeSpan.FromMinutes(10), Discharging, profile: "Quiet");
        Assert.Equal("Quiet", Assert.Single(kept).Profile);
    }

    [Fact]
    public void A_brightness_change_of_10_drops_the_interval()
    {
        var meter = new FakeMeter(40000) { Drop = 2500 };
        var r = new BatteryRecorder(meter);
        var kept = new List<BatteryInterval>();
        r.Recorded += kept.Add;
        var t = Run(r, T0, TimeSpan.FromMinutes(5), Discharging);
        meter.Brightness = 60;
        Run(r, t, TimeSpan.FromMinutes(6), Discharging);
        Assert.Empty(kept);
    }

    [Fact]
    public void Sleep_drops_the_interval_and_waits_a_minute_after_waking()
    {
        var meter = new FakeMeter(40000) { Drop = 2500 };
        var r = new BatteryRecorder(meter);
        var kept = new List<BatteryInterval>();
        r.Recorded += kept.Add;
        var t = Run(r, T0, TimeSpan.FromMinutes(5), Discharging);
        Assert.Equal(1, meter.Reads);
        t += TimeSpan.FromMinutes(30); // asleep
        t = Run(r, t, TimeSpan.FromSeconds(55), Discharging);
        Assert.Equal(1, meter.Reads);
        Run(r, t, TimeSpan.FromSeconds(10), Discharging);
        Assert.Equal(2, meter.Reads);
        Assert.Empty(kept);
    }

    [Fact]
    public void A_busy_ec_postpones_the_read()
    {
        var meter = new FakeMeter(40000);
        var r = new BatteryRecorder(meter);
        var t = Run(r, T0, TimeSpan.FromMinutes(3), Discharging, ecBusy: true);
        Assert.Equal(0, meter.Reads);
        Run(r, t, TimeSpan.FromSeconds(5), Discharging);
        Assert.Equal(1, meter.Reads);
    }

    [Fact]
    public void Csv_has_a_header_and_old_days_are_deleted()
    {
        string dir = Path.Combine(Path.GetTempPath(), "battery-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "20260901.csv"), "old");
            File.WriteAllText(Path.Combine(dir, "20261001.csv"), "recent");
            var log = new BatteryCsvLog(dir);
            log.Append(new BatteryInterval(new BatteryAverage(T0, TimeSpan.FromMinutes(10), 14.25), 55, PowerMode.Office, "Quiet", 50));
            var lines = File.ReadAllLines(Path.Combine(dir, "20261024.csv"));
            Assert.Equal([BatteryCsvLog.Header, "2026-10-24 12:00:00,10.0,14.25,55,Office,Quiet,50"], lines);
            Assert.False(File.Exists(Path.Combine(dir, "20260901.csv")));
            Assert.True(File.Exists(Path.Combine(dir, "20261001.csv")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Read_interval_setting_falls_back_to_10_minutes()
    {
        Assert.Equal(10, new ServiceSettings().BatteryReadMinutes);
        var (s, problems) = ServiceSettings.Parse("""{"version":2,"batteryReadMinutes":7}""");
        Assert.Equal(10, s.BatteryReadMinutes);
        Assert.NotEmpty(problems);
        Assert.Equal(30, ServiceSettings.Parse("""{"version":2,"batteryReadMinutes":30}""").Settings.BatteryReadMinutes);
        Assert.NotEmpty((new ServiceSettings() with { BatteryReadMinutes = 3 }).Validate());
    }

    [Fact]
    public void Estimates_weight_by_minutes_and_put_the_most_used_first()
    {
        var e = new BatteryEstimates();
        Assert.True(e.TryAddLine("2026-10-24 23:18:37,10.0,36.65,89,Office,Quiet,100"));
        Assert.True(e.TryAddLine("2026-10-25 18:31:40,30.0,40.00,87,Office,Quiet,100"));
        Assert.True(e.TryAddLine("2026-10-25 18:41:40,10.0,20.00,75,Office,Balanced,100"));
        Assert.False(e.TryAddLine("broken"));
        Assert.False(e.TryAddLine(BatteryCsvLog.Header));

        var all = e.All;
        Assert.Equal(("Quiet", 40.0), (all[0].Profile, all[0].Minutes));
        Assert.Equal((36.65 * 10 + 40 * 30) / 40, all[0].Watts, 3);
        Assert.Equal(PowerMode.Office, all[0].Mode);
        Assert.Equal("Balanced", all[1].Profile);
        Assert.Equal(3.14, all[1].Hours(62800)!.Value, 2);
    }

    [Fact]
    public void Estimates_are_read_back_from_the_csv_log()
    {
        string dir = Path.Combine(Path.GetTempPath(), "battery-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new BatteryCsvLog(dir);
            log.Append(new BatteryInterval(new BatteryAverage(T0, TimeSpan.FromMinutes(10), 14), 55, PowerMode.Office, "Quiet", 50));
            log.Append(new BatteryInterval(new BatteryAverage(T0.AddMinutes(10), TimeSpan.FromMinutes(10), 16), 50, PowerMode.Office, "Quiet", 50));
            var e = Assert.Single(BatteryEstimates.Load(dir).All);
            Assert.Equal((20.0, 15.0), (e.Minutes, e.Watts));
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class FakeMeter(int start) : IBatteryMeter
    {
        private int _left = start;
        public int Drop { get; set; }
        public int Brightness { get; set; } = 50;
        public int Reads { get; private set; }

        public BatteryReading? Read()
        {
            if (Reads++ > 0) _left -= Drop;
            return new BatteryReading(_left, Brightness);
        }
    }
}
