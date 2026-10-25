using Excalibur.Core.Automation;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;
using static Excalibur.Core.Tests.FanTestData;

namespace Excalibur.Core.Tests;

public sealed class AppRuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "excalibur-apprules-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceSettingsStore _settings;

    private static readonly AppRule Game = new("cs2.exe", "Performance", PowerMode.HighPerformance, 144);
    private static readonly AppRule Editor = new("Code.exe", "Quiet");

    public AppRuleTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new ServiceSettingsStore(Path.Combine(_dir, "fan.json"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static HashSet<string> Running(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    // ---- which rule is in force ---------------------------------------------------------------

    [Fact]
    public void The_program_started_last_wins_and_closing_it_goes_back_to_the_one_before()
    {
        var t = new AppRuleTracker();
        AppRule[] rules = [Game, Editor];
        Assert.False(t.Update(rules, Running("explorer.exe")));
        Assert.Null(t.Active);

        Assert.True(t.Update(rules, Running("code.exe")));
        Assert.Equal(Editor, t.Active);
        Assert.True(t.Update(rules, Running("code.exe", "CS2.EXE")));
        Assert.Equal(Game, t.Active);
        Assert.False(t.Update(rules, Running("code.exe", "cs2.exe")));

        Assert.True(t.Update(rules, Running("code.exe")));
        Assert.Equal(Editor, t.Active);
        Assert.True(t.Update(rules, Running()));
        Assert.Null(t.Active);
    }

    [Fact]
    public void Programs_already_running_count_in_rule_order_and_an_edited_rule_is_a_change()
    {
        var t = new AppRuleTracker();
        t.Update([Game, Editor], Running("cs2.exe", "code.exe"));
        Assert.Equal(Editor, t.Active);

        var edited = Editor with { FanProfile = "Balanced" };
        Assert.True(t.Update([Game, edited], Running("cs2.exe", "code.exe")));
        Assert.Equal(edited, t.Active);
        Assert.True(t.Update([Game], Running("cs2.exe", "code.exe"))); // the rule was deleted
        Assert.Equal(Game, t.Active);
    }

    [Fact]
    public void Rules_need_a_bare_exe_name_and_bad_ones_are_dropped_from_the_file()
    {
        Assert.Empty(Game.Validate());
        Assert.NotEmpty(new AppRule(@"C:\Games\cs2.exe").Validate());
        Assert.NotEmpty(new AppRule("cs2").Validate());
        Assert.NotEmpty(new AppRule("cs2.exe", RefreshHz: 10).Validate());
        Assert.NotEmpty(new AppRule("cs2.exe", CpuLimit: new CpuLimitOverride(new CpuLimit(5, 30))).Validate());

        var s = UserSettings.Parse("""
            { "appRules": [ { "program": "cs2.exe", "fanProfile": "Performance", "refreshHz": 144, "cpuLimit": { "limit": null } },
                            { "program": "CS2.exe" }, { "program": "bad" }, null ] }
            """);
        Assert.Equal([new AppRule("cs2.exe", "Performance", RefreshHz: 144, CpuLimit: new CpuLimitOverride(null))], s.AppRules);
        Assert.Equal(s, UserSettings.Parse(System.Text.Json.JsonSerializer.Serialize(s, UserSettings.Json)));
    }

    // ---- the fan profile layer ----------------------------------------------------------------

    private (ProfileSwitcher Switcher, FanControlLoop Loop) Profiles()
    {
        var loop = new FanControlLoop(new FakeFanHardware());
        loop.Start(Balanced, T0);
        return (new ProfileSwitcher(loop, _settings), loop);
    }

    [Fact]
    public void A_programs_profile_runs_without_being_saved_and_the_saved_one_comes_back()
    {
        var (switcher, loop) = Profiles();
        switcher.SetOverride("Performance", T0);
        Assert.Equal("Performance", loop.Status.Profile);
        Assert.Equal(FanSource.App, loop.Status.ProfileChangedBy);
        Assert.Equal("Balanced", _settings.Current.Profile);

        switcher.SetOverride(null, T0);
        Assert.Equal("Balanced", loop.Status.Profile);
        Assert.Null(switcher.Override);
    }

    [Fact]
    public void The_charger_rule_only_changes_the_saved_profile_while_a_program_rule_runs()
    {
        var (switcher, loop) = Profiles();
        switcher.SetOverride("Performance", T0);
        switcher.Switch("Quiet", FanSource.Automation);
        Assert.Equal("Performance", loop.Status.Profile);
        Assert.Equal("Quiet", _settings.Current.Profile);

        switcher.SetOverride(null, T0);
        Assert.Equal("Quiet", loop.Status.Profile);
    }

    [Fact]
    public void A_profile_the_user_picks_while_the_program_runs_stays_after_it_closes()
    {
        var (switcher, loop) = Profiles();
        switcher.SetOverride("Performance", T0);
        switcher.Switch("Quiet", FanSource.Tray);
        Assert.Null(switcher.Override);

        switcher.SetOverride(null, T0);
        Assert.Equal("Quiet", loop.Status.Profile);
        Assert.Equal("Quiet", _settings.Current.Profile);
    }

    [Fact]
    public void An_unknown_profile_in_a_rule_is_ignored()
    {
        var (switcher, loop) = Profiles();
        switcher.SetOverride("Turbo", T0);
        Assert.Equal("Balanced", loop.Status.Profile);
        Assert.Null(switcher.Override);
    }

    // ---- the power layer ----------------------------------------------------------------------

    private readonly PowerTests.FakePower _hw = new();
    private static readonly PowerSource Wall = new(false, 100, false);
    private static readonly PowerSource Battery = new(true, 70, false);

    private PowerManager Power(PowerSource source)
    {
        var power = new PowerManager(_hw, _settings, (_, _) => { });
        _hw.Source = source;
        power.Poll();
        return power;
    }

    [Fact]
    public void A_programs_mode_runs_without_becoming_the_choice_and_the_choice_comes_back()
    {
        var power = Power(Wall);
        power.SetMode(PowerMode.Office);
        power.SetAppOverride(PowerMode.HighPerformance, null);
        Assert.Equal(PowerMode.HighPerformance, _hw.Sent[^1]);
        Assert.Equal(PowerMode.Office, _settings.Current.PowerMode);

        power.SetAppOverride(null, null);
        Assert.Equal(PowerMode.Office, _hw.Sent[^1]);
    }

    [Fact]
    public void With_no_choice_the_mode_from_before_the_program_comes_back()
    {
        _hw.GpuWatts = 85; // Gaming, set by the firmware's own key or the stock app
        var power = Power(Wall);
        power.SetAppOverride(PowerMode.HighPerformance, null);
        power.SetAppOverride(null, null);
        Assert.Equal([PowerMode.HighPerformance, PowerMode.Gaming], _hw.Sent);
        Assert.Null(_settings.Current.PowerMode);
    }

    [Fact]
    public void On_battery_the_programs_mode_waits_for_the_charger()
    {
        var power = Power(Battery);
        power.SetAppOverride(PowerMode.HighPerformance, null);
        Assert.DoesNotContain(PowerMode.HighPerformance, _hw.Sent);

        _hw.Source = Wall;
        power.Poll();
        Assert.Equal(PowerMode.HighPerformance, _hw.Sent[^1]);
        _hw.GpuWatts = 100;
        _hw.Source = Battery;
        power.Poll();
        Assert.Equal(PowerMode.Office, _hw.Sent[^1]);
    }

    [Fact]
    public void The_charger_rules_mode_waits_under_a_programs_mode_and_follows_when_it_closes()
    {
        Assert.Empty(_settings.Update(s => s with
        {
            PowerAutomation = new PowerAutomation { Enabled = true, OnAc = new PowerRule { Mode = PowerMode.Gaming } },
        }, out _));
        var power = Power(Battery);
        power.SetAppOverride(PowerMode.HighPerformance, null);
        _hw.Source = Wall;
        power.Poll();
        Assert.Equal(PowerMode.HighPerformance, _hw.Sent[^1]);
        Assert.Equal(PowerMode.Gaming, _settings.Current.PowerMode);

        power.SetAppOverride(null, null);
        Assert.Equal(PowerMode.Gaming, _hw.Sent[^1]);
    }

    [Fact]
    public void A_mode_the_user_picks_while_the_program_runs_stays()
    {
        var power = Power(Wall);
        power.SetAppOverride(PowerMode.HighPerformance, null);
        power.SetMode(PowerMode.Gaming);
        _hw.Sent.Clear();
        power.SetAppOverride(null, null);
        Assert.Empty(_hw.Sent);
        Assert.Equal(PowerMode.Gaming, _settings.Current.PowerMode);
    }

    [Fact]
    public void A_programs_cpu_limit_holds_every_minute_and_the_saved_one_comes_back()
    {
        var power = Power(Wall);
        power.SetCpuLimit(new CpuLimit(25, 50));
        _hw.CpuLimits.Clear();

        power.SetAppOverride(null, new CpuLimitOverride(null)); // no limit while it runs
        power.SetAppOverride(null, new CpuLimitOverride(new CpuLimit(45, 90)));
        for (int i = 0; i < 30; i++) power.Poll(); // the minute check
        power.SetAppOverride(null, null);
        Assert.Equal([null, new CpuLimit(45, 90), new CpuLimit(45, 90), new CpuLimit(25, 50)], _hw.CpuLimits);
        Assert.Equal(new CpuLimit(25, 50), _settings.Current.CpuLimit);
    }

    [Fact]
    public void A_cpu_limit_the_user_sets_while_the_program_runs_stays()
    {
        var power = Power(Wall);
        power.SetAppOverride(null, new CpuLimitOverride(new CpuLimit(45, 90)));
        power.SetCpuLimit(new CpuLimit(20, 40));
        _hw.CpuLimits.Clear();
        power.SetAppOverride(null, null);
        Assert.Empty(_hw.CpuLimits);
        Assert.Equal(new CpuLimit(20, 40), _settings.Current.CpuLimit);
    }
}
