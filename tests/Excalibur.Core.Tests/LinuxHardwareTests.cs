using Excalibur.Core.Battery;
using Excalibur.Core.Lighting;
using Excalibur.Core.Power;
using Excalibur.Linux;

namespace Excalibur.Core.Tests;

public sealed class LinuxHardwareTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "exr-linux-tests-" + Guid.NewGuid().ToString("N"));

    public LinuxHardwareTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true); }
        catch { }
    }

    [Fact]
    public void Rapl_reads_watts_limits_and_restores_original_on_clear()
    {
        string pkg = Path.Combine(_temp, "intel-rapl_0");
        Directory.CreateDirectory(pkg);
        File.WriteAllText(Path.Combine(pkg, "name"), "package-0\n");
        File.WriteAllText(Path.Combine(pkg, "energy_uj"), "10000000\n");
        File.WriteAllText(Path.Combine(pkg, "max_energy_range_uj"), "100000000\n");
        File.WriteAllText(Path.Combine(pkg, "constraint_0_power_limit_uw"), "45000000\n");
        File.WriteAllText(Path.Combine(pkg, "constraint_1_power_limit_uw"), "115000000\n");

        var rapl = new LinuxRapl(_temp);
        var first = rapl.Read(TimeSpan.FromSeconds(1));
        Assert.NotNull(first);
        Assert.Null(first.Watts);
        Assert.Equal(45, first.Pl1Watts);
        Assert.Equal(115, first.Pl2Watts);
        Assert.Equal(45, first.ModePl1Watts);

        // +36 J over 2 seconds = 18 W
        File.WriteAllText(Path.Combine(pkg, "energy_uj"), "46000000\n");
        var second = rapl.Read(TimeSpan.FromSeconds(3));
        Assert.NotNull(second);
        Assert.Equal(18, second.Watts);

        // Apply custom limit 35 / 60 W; original 45 W stays in ModePl1Watts
        rapl.Apply(new CpuLimit(35, 60));
        Assert.Equal("35000000", File.ReadAllText(Path.Combine(pkg, "constraint_0_power_limit_uw")).Trim());
        Assert.Equal("60000000", File.ReadAllText(Path.Combine(pkg, "constraint_1_power_limit_uw")).Trim());

        var limited = rapl.Read(TimeSpan.FromSeconds(4));
        Assert.NotNull(limited);
        Assert.Equal(35, limited.Pl1Watts);
        Assert.Equal(60, limited.Pl2Watts);
        Assert.Equal(45, limited.ModePl1Watts);

        // Clear restores 45 / 115 W
        rapl.Apply(null);
        Assert.Equal("45000000", File.ReadAllText(Path.Combine(pkg, "constraint_0_power_limit_uw")).Trim());
        Assert.Equal("115000000", File.ReadAllText(Path.Combine(pkg, "constraint_1_power_limit_uw")).Trim());
    }

    [Fact]
    public void Rapl_never_writes_the_mmio_copy_that_the_power_mode_owns()
    {
        string pkg = Path.Combine(_temp, "intel-rapl_0");
        string mmio = Path.Combine(_temp, "intel-rapl-mmio_0");
        Directory.CreateDirectory(pkg);
        Directory.CreateDirectory(mmio);
        File.WriteAllText(Path.Combine(pkg, "name"), "package-0\n");
        File.WriteAllText(Path.Combine(pkg, "constraint_0_power_limit_uw"), "200000000\n");
        File.WriteAllText(Path.Combine(pkg, "constraint_1_power_limit_uw"), "115000000\n");
        File.WriteAllText(Path.Combine(mmio, "name"), "package-0\n");
        File.WriteAllText(Path.Combine(mmio, "constraint_0_power_limit_uw"), "55000000\n"); // High performance

        var rapl = new LinuxRapl(_temp);
        rapl.Apply(new CpuLimit(35, 60));
        Assert.Equal("55000000", File.ReadAllText(Path.Combine(mmio, "constraint_0_power_limit_uw")).Trim());
        Assert.Equal(35, rapl.Read(TimeSpan.FromSeconds(1))!.Pl1Watts); // the lower copy (MSR) holds the limit

        // The EC moves to Office (35 W) while the limit runs; clearing the limit must leave that alone, not bring 55 W back.
        File.WriteAllText(Path.Combine(mmio, "constraint_0_power_limit_uw"), "35000000\n");
        rapl.Apply(null);
        Assert.Equal("35000000", File.ReadAllText(Path.Combine(mmio, "constraint_0_power_limit_uw")).Trim());
        Assert.Equal("200000000", File.ReadAllText(Path.Combine(pkg, "constraint_0_power_limit_uw")).Trim());
    }

    [Fact]
    public void Rapl_handles_energy_counter_wraparound()
    {
        string pkg = Path.Combine(_temp, "intel-rapl_0");
        Directory.CreateDirectory(pkg);
        File.WriteAllText(Path.Combine(pkg, "name"), "package-0\n");
        File.WriteAllText(Path.Combine(pkg, "energy_uj"), "90000000\n");
        File.WriteAllText(Path.Combine(pkg, "max_energy_range_uj"), "100000000\n");
        File.WriteAllText(Path.Combine(pkg, "constraint_0_power_limit_uw"), "45000000\n");
        File.WriteAllText(Path.Combine(pkg, "constraint_1_power_limit_uw"), "115000000\n");

        var rapl = new LinuxRapl(_temp);
        _ = rapl.Read(TimeSpan.FromSeconds(1));

        // Wrapped past 100_000_000 to 10_000_000 -> delta = 20_000_000 uJ (20 J) over 1 s = 20 W
        File.WriteAllText(Path.Combine(pkg, "energy_uj"), "10000000\n");
        var second = rapl.Read(TimeSpan.FromSeconds(2));
        Assert.NotNull(second);
        Assert.Equal(20, second.Watts);
    }

    [Fact]
    public void PowerHardware_reads_power_supply_and_writes_platform_profile()
    {
        string psu = Path.Combine(_temp, "psu");
        string ac = Path.Combine(psu, "ADP1");
        string bat = Path.Combine(psu, "BAT0");
        Directory.CreateDirectory(ac);
        Directory.CreateDirectory(bat);
        File.WriteAllText(Path.Combine(ac, "type"), "Mains\n");
        File.WriteAllText(Path.Combine(ac, "online"), "1\n");
        File.WriteAllText(Path.Combine(bat, "type"), "Battery\n");
        File.WriteAllText(Path.Combine(bat, "capacity"), "82\n");
        File.WriteAllText(Path.Combine(bat, "status"), "Charging\n");

        string profileFile = Path.Combine(_temp, "platform_profile");
        File.WriteAllText(profileFile, "balanced\n");

        var hw = new LinuxPowerHardware(null, null, null, psu, profileFile);
        var sourceAc = hw.ReadSource();
        Assert.False(sourceAc.OnBattery);
        Assert.Equal(82, sourceAc.BatteryPercent);

        File.WriteAllText(Path.Combine(ac, "online"), "0\n");
        File.WriteAllText(Path.Combine(bat, "status"), "Discharging\n");
        var sourceBat = hw.ReadSource();
        Assert.True(sourceBat.OnBattery);

        hw.SetMode(PowerMode.HighPerformance);
        Assert.Equal("performance", File.ReadAllText(profileFile).Trim());
        hw.SetMode(PowerMode.Office);
        Assert.Equal("low-power", File.ReadAllText(profileFile).Trim());
    }

    [Fact]
    public void BatteryHardware_reads_health_live_remaining_time_and_meter()
    {
        string psu = Path.Combine(_temp, "bat_psu");
        string bat = Path.Combine(psu, "BAT0");
        string bl = Path.Combine(_temp, "backlight", "intel_backlight");
        Directory.CreateDirectory(bat);
        Directory.CreateDirectory(bl);

        File.WriteAllText(Path.Combine(bat, "type"), "Battery\n");
        File.WriteAllText(Path.Combine(bat, "energy_full_design"), "61500000\n");
        File.WriteAllText(Path.Combine(bat, "energy_full"), "60000000\n");
        File.WriteAllText(Path.Combine(bat, "energy_now"), "30000000\n");
        File.WriteAllText(Path.Combine(bat, "power_now"), "15000000\n");
        File.WriteAllText(Path.Combine(bat, "capacity"), "50\n");
        File.WriteAllText(Path.Combine(bat, "status"), "Discharging\n");
        File.WriteAllText(Path.Combine(bat, "technology"), "Li-ion\n");
        File.WriteAllText(Path.Combine(bat, "cycle_count"), "42\n");

        File.WriteAllText(Path.Combine(bl, "brightness"), "120\n");
        File.WriteAllText(Path.Combine(bl, "max_brightness"), "240\n");

        var hw = new LinuxBatteryHardware(psu, Path.Combine(_temp, "backlight"));
        var health = hw.ReadHealth();
        Assert.NotNull(health);
        Assert.Equal(61500, health.DesignedMWh);
        Assert.Equal(60000, health.FullMWh);
        Assert.Equal("Li-ion", health.Chemistry);
        Assert.Equal(42, health.Cycles);
        Assert.Equal(98, health.Percent);

        var live = hw.ReadLive();
        Assert.NotNull(live);
        Assert.Equal(50, live.Value.Percent);
        Assert.Equal(BatteryState.OnBattery, live.Value.State);
        // 30 Wh at 15 W = 120 minutes
        Assert.Equal(120, live.Value.RemainingMinutes);
        Assert.Equal(15.0, hw.ReadWattsNow());

        var reading = hw.Read();
        Assert.NotNull(reading);
        Assert.Equal(30000, reading.Value.RemainingMWh);
        Assert.Equal(50, reading.Value.Brightness);
    }

    [Fact]
    public void EcSmiChannel_writes_keyboard_led_registers_and_maps_brightness()
    {
        var regs = new byte[256];
        var writtenControl = new List<byte>();

        byte ReadEc(byte addr) => regs[addr];
        void WriteEc(byte addr, byte val)
        {
            regs[addr] = val;
            if (addr == LinuxEcSmiChannel.RegControl)
            {
                writtenControl.Add(val);
                // Simulate EC acknowledging LKCC (bit 0) or LKBS (bit 3) by setting LKBE (bit 1)
                if ((val & 0x09) != 0) regs[addr] = (byte)(val | 0x02);
            }
        }

        var smi = new LinuxEcSmiChannel(ReadEc, WriteEc);
        var light = new KeyboardLight(smi);

        // Static #123456 at brightness 2 on left zone (zone 5)
        light.Set(LightZone.KeyboardLeft, new LightSetting(LightEffect.Static, 2, 0x12, 0x34, 0x56));
        Assert.Equal(0x12, regs[LinuxEcSmiChannel.RegRed]);
        Assert.Equal(0x34, regs[LinuxEcSmiChannel.RegGreen]);
        Assert.Equal(0x56, regs[LinuxEcSmiChannel.RegBlue]);
        // Brightness 2 maps to LKBL = 1; Effect Static = 1 -> 0xC1 = 0x11
        Assert.Equal(0x11, regs[LinuxEcSmiChannel.RegModeLevel]);
        // Control writes for zone 5 (0x50): 0x50, then 0x51 (LKCC), then 0x58 (LKBS)
        Assert.Equal(new byte[] { 0x50, 0x51, 0x58 }, writtenControl);

        // Key brightness level mapping from EC 0x48 (LKCL): 0->1, 1->2, 2->0
        regs[LinuxEcSmiChannel.RegKeyLevel] = 0;
        Assert.Equal(1, light.ReadKeyBrightness());
        regs[LinuxEcSmiChannel.RegKeyLevel] = 1;
        Assert.Equal(2, light.ReadKeyBrightness());
        regs[LinuxEcSmiChannel.RegKeyLevel] = 2;
        Assert.Equal(0, light.ReadKeyBrightness());
    }
}
