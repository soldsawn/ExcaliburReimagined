using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.Core.Tests;

public sealed class PowerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "excalibur-power-" + Guid.NewGuid().ToString("N"));
    private readonly FakePower _hw = new();
    private readonly ServiceSettingsStore _settings;
    private readonly PowerManager _power;
    private readonly List<(string Profile, string Source)> _profiles = [];
    private readonly List<string> _log = [];

    private static readonly PowerSource Wall = new(false, 100, false);
    private static readonly PowerSource Battery = new(true, 70, false);
    private static readonly PowerSource UsbC = new(false, 100, true);

    public PowerTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new ServiceSettingsStore(Path.Combine(_dir, "fan.json"));
        _power = new PowerManager(_hw, _settings, (p, s) => _profiles.Add((p, s)), _log.Add);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Poll(PowerSource source)
    {
        _hw.Source = source;
        _power.Poll();
    }

    private void Automate(PowerAutomation automation) =>
        Assert.Empty(_settings.Update(s => s with { PowerAutomation = automation with { Enabled = true } }, out _));

    // ---- the lock ---------------------------------------------------------------------------

    [Fact]
    public void Nothing_is_written_until_the_user_chooses_something()
    {
        Poll(Wall);
        Poll(Battery);
        Poll(Wall);
        Assert.Empty(_hw.Sent);
    }

    [Fact]
    public void Starting_on_battery_forces_office_only_when_a_faster_mode_is_active()
    {
        _hw.GpuWatts = 75; // already Office
        Poll(Battery);
        Assert.Empty(_hw.Sent);

        _power.Reapply(); // nothing chosen: still no write
        Assert.Empty(_hw.Sent);

        var other = new FakePower { GpuWatts = 100, Source = Battery };
        new PowerManager(other, _settings, (_, _) => { }).Poll();
        Assert.Equal([PowerMode.Office], other.Sent);
    }

    [Fact]
    public void Fast_modes_are_refused_on_battery_and_with_a_weak_usb_c_adapter()
    {
        Poll(Battery);
        var onBattery = _power.SetMode(PowerMode.Gaming);
        Assert.False(onBattery.Ok);
        Assert.Equal("power_battery", onBattery.Code);

        Poll(UsbC);
        var weak = _power.SetMode(PowerMode.HighPerformance);
        Assert.Equal("power_usbc", weak.Code);

        Assert.Empty(_hw.Sent);
        Assert.True(_power.SetMode(PowerMode.Office).Ok); // Office is always fine
        Assert.Equal([PowerMode.Office], _hw.Sent);
    }

    [Fact]
    public void The_users_choice_survives_a_trip_on_battery()
    {
        Poll(Wall);
        Assert.True(_power.SetMode(PowerMode.Gaming).Ok);
        Assert.Equal(PowerMode.Gaming, _settings.Current.PowerMode);

        Poll(Battery);
        Assert.Equal(PowerMode.Gaming, _settings.Current.PowerMode); // preference untouched
        Assert.Equal([PowerMode.Gaming, PowerMode.Office], _hw.Sent);

        Poll(Wall);
        Assert.Equal([PowerMode.Gaming, PowerMode.Office, PowerMode.Gaming], _hw.Sent);
    }

    [Fact]
    public void Office_chosen_on_battery_is_not_remembered_as_a_preference()
    {
        Poll(Battery);
        _power.SetMode(PowerMode.Office);
        Assert.Null(_settings.Current.PowerMode);
    }

    [Fact]
    public void A_failing_firmware_is_reported_and_the_choice_is_not_stored()
    {
        Poll(Wall);
        _hw.FailSet = true;
        var result = _power.SetMode(PowerMode.HighPerformance);
        Assert.False(result.Ok);
        Assert.Equal("power_failed", result.Code);
        Assert.Null(_settings.Current.PowerMode);
    }

    [Fact]
    public void A_percent_change_alone_does_not_count_as_a_new_source()
    {
        Poll(Wall);
        _power.SetMode(PowerMode.Gaming);
        _hw.Sent.Clear();
        Poll(Wall with { BatteryPercent = 98 });
        Poll(Wall with { BatteryPercent = 97 });
        Assert.Empty(_hw.Sent);
    }

    // ---- automation -------------------------------------------------------------------------

    [Fact]
    public void Automation_off_never_switches_the_fan_profile()
    {
        Poll(Wall);
        _power.SetMode(PowerMode.Gaming);
        Poll(Battery);
        Poll(Wall);
        Assert.Empty(_profiles);
    }

    [Fact]
    public void Automation_applies_each_sides_mode_and_fan_profile_on_a_real_change()
    {
        Automate(new PowerAutomation
        {
            OnAc = new PowerRule { Mode = PowerMode.HighPerformance, FanProfile = "Performance" },
            OnBattery = new PowerRule { Mode = PowerMode.Office, FanProfile = "Quiet" },
        });

        Poll(Wall); // service start: the mode is applied, the profile is not switched
        Assert.Equal([PowerMode.HighPerformance], _hw.Sent);
        Assert.Empty(_profiles);

        Poll(Battery);
        Assert.Equal([("Quiet", "automation")], _profiles);
        Poll(Wall);
        Assert.Equal([("Quiet", "automation"), ("Performance", "automation")], _profiles);
        Assert.Equal([PowerMode.HighPerformance, PowerMode.Office, PowerMode.HighPerformance], _hw.Sent);
    }

    [Fact]
    public void A_rule_with_only_a_fan_profile_leaves_the_mode_alone()
    {
        Automate(new PowerAutomation { OnAc = new PowerRule { FanProfile = "Balanced" } });
        Poll(Battery);
        _hw.Sent.Clear();
        Poll(Wall);
        Assert.Empty(_hw.Sent);
        Assert.Equal([("Balanced", "automation")], _profiles);
    }

    [Fact]
    public void An_unknown_fan_profile_or_a_fast_battery_mode_is_rejected_when_saving()
    {
        var unknown = _settings.Update(s => s with { PowerAutomation = new PowerAutomation { OnAc = new PowerRule { FanProfile = "Turbo" } } }, out _);
        Assert.NotEmpty(unknown);
        var fastOnBattery = _settings.Update(s => s with { PowerAutomation = new PowerAutomation { OnBattery = new PowerRule { Mode = PowerMode.Gaming } } }, out _);
        Assert.NotEmpty(fastOnBattery);
    }

    [Fact]
    public void Resume_applies_the_mode_again_without_switching_profiles()
    {
        Automate(new PowerAutomation { OnAc = new PowerRule { Mode = PowerMode.Gaming, FanProfile = "Performance" } });
        Poll(Wall);
        _hw.Sent.Clear();
        _power.Reapply();
        Assert.Equal([PowerMode.Gaming], _hw.Sent);
        Assert.Empty(_profiles);
    }

    // ---- status and settings ----------------------------------------------------------------

    [Fact]
    public void Status_reads_the_active_mode_from_the_gpu_limit()
    {
        Poll(Wall);
        _hw.GpuWatts = 85;
        _power.Poll();
        Assert.Equal(PowerMode.Gaming, _power.Status.Mode);

        _hw.GpuWatts = null;
        _power.Poll();
        Assert.Null(_power.Status.Mode);

        Poll(Battery);
        Assert.Equal(PowerLock.OnBattery, _power.Status.Lock);
    }

    [Theory]
    [InlineData(100, PowerMode.HighPerformance)]
    [InlineData(85, PowerMode.Gaming)]
    [InlineData(75, PowerMode.Office)]
    [InlineData(76, PowerMode.Office)]
    public void Gpu_watts_map_to_modes(int watts, PowerMode expected) => Assert.Equal(expected, PowerModeInfo.FromGpuWatts(watts));

    [Fact]
    public void Gpu_watts_between_modes_match_none() => Assert.Null(PowerModeInfo.FromGpuWatts(70));

    [Fact]
    public void Power_settings_round_trip_and_bad_parts_fall_back_one_by_one()
    {
        var original = new ServiceSettings
        {
            PowerMode = PowerMode.Gaming,
            PowerAutomation = new PowerAutomation
            {
                Enabled = true,
                OnAc = new PowerRule { Mode = PowerMode.HighPerformance, FanProfile = "Performance" },
                OnBattery = new PowerRule { Mode = PowerMode.Office, FanProfile = "Quiet" },
            },
        };
        var (back, problems) = ServiceSettings.Parse(original.ToJson());
        Assert.Equal(original, back);
        Assert.Empty(problems);

        var (bad, badProblems) = ServiceSettings.Parse("""
            {"powerMode":"Turbo","powerAutomation":{"enabled":true,"onAc":{"fanProfile":"Nope"},"onBattery":{"mode":"Gaming"}}}
            """);
        Assert.Null(bad.PowerMode);
        Assert.True(bad.PowerAutomation.Enabled);
        Assert.Null(bad.PowerAutomation.OnAc.FanProfile);
        Assert.Equal(PowerMode.Office, bad.PowerAutomation.OnBattery.Mode);
        Assert.Equal(3, badProblems.Count);
        Assert.Empty(bad.Validate());
    }

    private sealed class FakePower : IPowerHardware
    {
        public PowerSource Source { get; set; } = new(false, 100, false);
        public int? GpuWatts { get; set; } = 70;
        public bool FailSet { get; set; }
        public List<PowerMode> Sent { get; } = [];

        public void SetMode(PowerMode mode)
        {
            if (FailSet) throw new PowerException("firmware says no");
            Sent.Add(mode);
        }

        public PowerSource ReadSource() => Source;
        public int? ReadGpuWatts() => GpuWatts;
    }
}
