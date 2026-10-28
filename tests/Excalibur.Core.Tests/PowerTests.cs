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
    public void Cpu_power_is_read_on_every_poll_even_when_the_source_stays()
    {
        _hw.Cpu = new CpuPower(null, 45, 115);
        _power.Poll();
        _hw.Cpu = new CpuPower(23, 45, 115);
        _power.Poll();
        Assert.Equal(new CpuPower(23, 45, 115), _power.Status.Cpu);
        Assert.Equal(23, Excalibur.Core.Fan.Ipc.PowerStatusDto.From(_power.Status).CpuWatts);
    }

    [Fact]
    public void A_cpu_limit_is_written_saved_and_put_back_when_cleared()
    {
        Assert.True(_power.SetCpuLimit(new CpuLimit(25, 35)).Ok);
        Assert.Equal(new CpuLimit(25, 35), _settings.Current.CpuLimit);
        Assert.Equal(new CpuLimit(25, 35), _power.Status.CpuLimit);
        Assert.True(_power.SetCpuLimit(null).Ok);
        Assert.Null(_settings.Current.CpuLimit);
        Assert.Equal([new CpuLimit(25, 35), null], _hw.CpuLimits);
    }

    [Fact]
    public void An_invalid_or_failed_cpu_limit_is_not_saved()
    {
        Assert.Equal("cpu_limit_invalid", _power.SetCpuLimit(new CpuLimit(5, 35)).Code);
        _hw.FailCpuLimit = true;
        Assert.Equal("cpu_limit_failed", _power.SetCpuLimit(new CpuLimit(25, 35)).Code);
        Assert.Null(_settings.Current.CpuLimit);
    }

    [Fact]
    public void The_saved_cpu_limit_is_applied_at_start_every_minute_after_resume_and_released_on_stop()
    {
        _power.SetCpuLimit(new CpuLimit(25, 35));
        _hw.CpuLimits.Clear();
        for (int i = 0; i < 31; i++) _power.Poll(); // polls 1 and 31 check it
        _power.Reapply();
        _power.RestoreCpuLimit();
        Assert.Equal([new CpuLimit(25, 35), new CpuLimit(25, 35), new CpuLimit(25, 35), null], _hw.CpuLimits);
        Assert.Equal(new CpuLimit(25, 35), _settings.Current.CpuLimit); // the choice stays for the next start
    }

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
    public void A_change_of_source_waits_for_the_ec_to_settle_before_the_mode_and_profile_follow()
    {
        // 2026-10-23 19:20: the charger plugged in, the mode sent at once, the EC timed out (ACPI 13) 5 s later.
        Automate(new PowerAutomation { OnAc = new PowerRule { Mode = PowerMode.Gaming, FanProfile = "Balanced" } });
        var power = new PowerManager(_hw, _settings, (p, s) => _profiles.Add((p, s)), _log.Add, settle: TimeSpan.FromMilliseconds(300));
        _hw.Source = Battery;
        power.Poll(); // start: acts at once
        int sent = _hw.Sent.Count;

        _hw.Source = Wall;
        power.Poll();
        Assert.Equal(sent, _hw.Sent.Count);
        Assert.Empty(_profiles);

        Thread.Sleep(400);
        power.Poll();
        Assert.Equal(PowerMode.Gaming, _hw.Sent[^1]);
        Assert.Equal([("Balanced", "automation")], _profiles);
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

    [Fact]
    public void The_mode_sent_is_shown_while_the_gpu_limit_wanders()
    {
        Poll(Wall);
        Assert.True(_power.SetMode(PowerMode.Gaming).Ok);
        _hw.GpuWatts = 75; // Dynamic Boost holding Gaming down to Office's limit
        _power.Poll();
        Assert.Equal(PowerMode.Gaming, _power.Status.Mode);
    }

    [Theory]
    [InlineData(100, PowerMode.HighPerformance)]
    [InlineData(85, PowerMode.Gaming)]
    [InlineData(75, PowerMode.Office)]
    [InlineData(76, PowerMode.Office)]
    public void Gpu_watts_map_to_modes(int watts, PowerMode expected) => Assert.Equal(expected, new PowerModeWatts().FromGpuWatts(watts));

    [Fact]
    public void Gpu_watts_between_modes_match_none() => Assert.Null(new PowerModeWatts().FromGpuWatts(70));

    // ---- what each mode allows here ----------------------------------------------------------

    private void PollMany(int count)
    {
        for (int i = 0; i < count; i++) _power.Poll();
    }

    [Fact]
    public void A_mode_is_measured_a_few_seconds_after_it_was_sent_on_the_wall()
    {
        Poll(Wall);
        _hw.Cpu = new CpuPower(20, 30, 115, ModePl1Watts: 60);
        Assert.True(_power.SetMode(PowerMode.Gaming).Ok);
        _hw.GpuWatts = 140; // another laptop's card
        PollMany(6);
        Assert.Equal(new ModeWatts(PowerMode.Gaming, 60, 140), _power.Status.Modes!.Single(m => m.Mode == PowerMode.Gaming));
        Assert.Equal(PowerMode.Gaming, _power.Status.Mode);
        // Not a G870: the modes not measured yet show nothing rather than the G870's watts.
        Assert.Equal(new ModeWatts(PowerMode.Office, null, null), _power.Status.Modes!.Single(m => m.Mode == PowerMode.Office));
    }

    [Fact]
    public void A_g870_shows_its_known_watts_once_one_mode_matched_them()
    {
        Assert.All(_power.Status.Modes!, m => Assert.Null(m.GpuWatts)); // nothing measured yet
        Poll(Wall);
        Assert.True(_power.SetMode(PowerMode.Gaming).Ok);
        _hw.GpuWatts = 85;
        _hw.Cpu = new CpuPower(20, 45, 115, ModePl1Watts: 45);
        PollMany(6);
        Assert.Equal(
            [new ModeWatts(PowerMode.HighPerformance, 55, 100), new ModeWatts(PowerMode.Gaming, 45, 85), new ModeWatts(PowerMode.Office, 35, 75)],
            _power.Status.Modes);
    }

    [Fact]
    public void Battery_limits_are_not_taken_for_the_mode()
    {
        Poll(Battery);
        Assert.True(_power.SetMode(PowerMode.Office).Ok);
        _hw.GpuWatts = 50;
        PollMany(6);
        Assert.All(_power.Status.Modes!, m => Assert.Null(m.GpuWatts));
    }

    [Fact]
    public void A_measured_mode_wins_over_the_g870_value_it_shares()
    {
        // A card whose Office is 100 W: that is not High performance here.
        var modes = new PowerModeWatts();
        modes.Learn(PowerMode.Office, 100, 45);
        Assert.Equal(PowerMode.Office, modes.FromGpuWatts(100));
        Assert.Null(modes.FromGpuWatts(140));
    }

    [Fact]
    public void A_mode_keeps_the_highest_limits_measured()
    {
        Poll(Wall);
        Assert.True(_power.SetMode(PowerMode.Gaming).Ok);
        _hw.GpuWatts = 85;
        _hw.Cpu = new CpuPower(20, 45, 115, ModePl1Watts: 45);
        PollMany(6);
        _hw.GpuWatts = 73; // held down under load
        _hw.Cpu = new CpuPower(20, 35, 115, ModePl1Watts: 35);
        PollMany(6);
        Assert.Equal(new ModeWatts(PowerMode.Gaming, 45, 85), _power.Status.Modes!.Single(m => m.Mode == PowerMode.Gaming));
    }

    [Fact]
    public void Measurements_are_kept_in_their_file()
    {
        string path = Path.Combine(_dir, PowerModeWatts.FileName);
        new PowerModeWatts(path).Learn(PowerMode.Gaming, 140, null);
        Assert.Equal(new ModeWatts(PowerMode.Gaming, null, 140), new PowerModeWatts(path).All.Single(m => m.Mode == PowerMode.Gaming));
    }

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

    internal sealed class FakePower : IPowerHardware
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
        public CpuPower? Cpu { get; set; }
        public CpuPower? ReadCpuPower() => Cpu;
        public bool FailCpuLimit { get; set; }
        public List<CpuLimit?> CpuLimits { get; } = [];
        public void ApplyCpuLimit(CpuLimit? limit)
        {
            if (FailCpuLimit) throw new PowerException("MSR write failed");
            CpuLimits.Add(limit);
        }
    }
}
