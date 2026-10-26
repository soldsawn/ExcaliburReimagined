using Excalibur.Core.Fan;
using Xunit;
using static Excalibur.Core.Tests.FanTestData;

namespace Excalibur.Core.Tests;

public class EcTrialTests
{
    private readonly FakeFanHardware _hw = new() { EcVersion = "P010A0" };
    private readonly FanControlLoop _loop;
    private readonly List<string> _trusted = [];
    private FanProfile? _trustedProfile;
    private FanWritePath? _trustedPath;

    public EcTrialTests() : this(new FanLoopOptions()) { }

    private EcTrialTests(FanLoopOptions options)
    {
        _loop = new FanControlLoop(_hw, options);
        _loop.EcTrusted += (v, profile, path) => { _trusted.Add(v); _trustedProfile = profile; _trustedPath = path; };
        _loop.Start(FanProfile.Balanced, T0);
        TickAt(1);
    }

    private FanStatus TickAt(int second) => _loop.Tick(T0.AddSeconds(second));

    private FanStatus RunUntilDone(int from)
    {
        for (int i = from; i < from + 60; i++)
            if (TickAt(i).Trial is { State: not EcTrialState.Running } done) return _loop.Status;
        return _loop.Status;
    }

    [Fact]
    public void Both_fans_following_trusts_the_version_and_hands_another_model_to_the_ec()
    {
        _loop.StartEcTrial(T0.AddSeconds(2));
        var s = RunUntilDone(2);

        Assert.Equal(EcTrialState.Passed, s.Trial!.State);
        Assert.True(s.Trial.CpuFollows);
        Assert.True(s.Trial.GpuFollows);
        Assert.Equal(["P010A0"], _trusted);
        Assert.Equal(FanLoopMode.Running, s.Mode);
        // Each fan got one test percent and was handed back before the next.
        var trial = _hw.Writes.Skip(2).Take(4).ToList();
        Assert.Equal([FanId.Cpu, FanId.Cpu, FanId.Gpu, FanId.Gpu], trial.Select(w => w.Fan));
        Assert.False(trial[0].Target.IsAuto);
        Assert.True(trial[1].Target.IsAuto);
        Assert.True(trial[3].Target.IsAuto);

        // The built-in curves were tuned on P008B0: another model starts on EC automatic.
        Assert.Equal(FanProfile.EcAuto.Name, s.Profile);
        Assert.Equal(FanProfile.EcAuto, _trustedProfile);
        TickAt(80);
        Assert.True(_hw.CpuTarget.IsAuto);
        Assert.True(_hw.GpuTarget.IsAuto);
    }

    [Fact]
    public void A_version_the_curves_were_tuned_on_keeps_its_profile_after_the_trial()
    {
        var t = new EcTrialTests(new FanLoopOptions { CurveEcVersions = new HashSet<string> { "P010A0" } });
        t._loop.StartEcTrial(T0.AddSeconds(2));
        var s = t.RunUntilDone(2);

        Assert.Equal(EcTrialState.Passed, s.Trial!.State);
        Assert.Equal(FanProfile.Balanced.Name, s.Profile);
        t.TickAt(80);
        Assert.False(t._hw.CpuTarget.IsAuto); // the profile now runs
    }

    [Fact]
    public void P009C0_keeps_the_g870_curves_after_its_trial() => Assert.Contains("P009C0", new FanLoopOptions().CurveEcVersions);

    [Theory]
    [InlineData("P003K0", true)]
    [InlineData("P009C0", true)]
    [InlineData("P010A0", false)]
    public void A_version_seen_on_another_machine_is_flagged_while_unverified(string version, bool expected)
    {
        var hw = new FakeFanHardware { EcVersion = version };
        var loop = new FanControlLoop(hw);
        loop.Start(FanProfile.Balanced, T0);
        Assert.Equal(expected, loop.Tick(T0.AddSeconds(1)).EcSeenElsewhere);
    }

    [Fact]
    public void A_fan_that_does_not_follow_fails_and_both_go_back_to_the_ec()
    {
        _hw.DeafFan = FanId.Gpu;
        _loop.StartEcTrial(T0.AddSeconds(2));
        var s = RunUntilDone(2);

        Assert.Equal(EcTrialState.Failed, s.Trial!.State);
        Assert.Equal(EcTrialProblem.NoFollow, s.Trial.Problem);
        Assert.True(s.Trial.CpuFollows);
        Assert.False(s.Trial.GpuFollows);
        Assert.Empty(_trusted);
        Assert.Equal(FanLoopMode.MonitorOnly, s.Mode);
        Assert.True(_hw.CpuTarget.IsAuto);
        Assert.True(_hw.GpuTarget.IsAuto);
        Assert.False(s.HoldsFans);
    }

    [Fact]
    public void It_waits_15_s_for_wmi_and_15_s_for_a_fan_before_failing()
    {
        _hw.DeafFan = FanId.Cpu;
        _loop.StartEcTrial(T0.AddSeconds(2));
        TickAt(2); // writes both targets through WMI
        for (int i = 3; i <= 16; i++) Assert.True(TickAt(i).HoldsFans);
        var s = TickAt(17); // WMI did nothing: handed back, the EC is next
        Assert.Equal(EcTrialState.Running, s.Trial!.State);
        Assert.False(s.Trial.WmiFollows);
        Assert.Equal(FanWritePath.Ec, _hw.WritePath);

        TickAt(18); // writes the CPU target
        for (int i = 19; i <= 32; i++) Assert.Equal(EcTrialState.Running, TickAt(i).Trial!.State);
        Assert.True(TickAt(32).HoldsFans);
        Assert.Equal(EcTrialState.Failed, TickAt(33).Trial!.State);
    }

    [Fact]
    public void Fans_that_follow_wmi_pass_without_touching_the_ec_registers()
    {
        _hw.WmiWorks = true;
        int startWrites = _hw.Writes.Count;
        _loop.StartEcTrial(T0.AddSeconds(2));
        var s = RunUntilDone(2);

        Assert.Equal(EcTrialState.Passed, s.Trial!.State);
        Assert.True(s.Trial.WmiFollows);
        Assert.Null(s.Trial.CpuFollows);
        Assert.Equal(FanWritePath.Wmi, s.Trial.Path);
        Assert.Equal(FanWritePath.Wmi, _trustedPath);
        Assert.Equal(FanWritePath.Wmi, s.WritePath);
        Assert.Equal(startWrites, _hw.Writes.Count);
        // Both fans moved together, then both were handed back.
        Assert.Equal([FanId.Cpu, FanId.Gpu, FanId.Cpu, FanId.Gpu], _hw.WmiWrites.Select(w => w.Fan));
        Assert.All(_hw.WmiWrites.Take(2), w => Assert.False(w.Target.IsAuto));
        Assert.All(_hw.WmiWrites.Skip(2), w => Assert.True(w.Target.IsAuto));
    }

    [Fact]
    public void Fans_that_ignore_wmi_are_tried_on_the_ec()
    {
        _loop.StartEcTrial(T0.AddSeconds(2));
        var s = RunUntilDone(2);

        Assert.Equal(EcTrialState.Passed, s.Trial!.State);
        Assert.False(s.Trial.WmiFollows);
        Assert.True(s.Trial.GpuFollows);
        Assert.Equal(FanWritePath.Ec, s.Trial.Path);
        Assert.Equal(FanWritePath.Ec, _trustedPath);
        Assert.Equal(FanWritePath.Ec, s.WritePath);
    }

    [Fact]
    public void A_failed_trial_leaves_the_fans_on_the_ec_path()
    {
        _hw.WmiWorks = true;
        _loop.StartEcTrial(T0.AddSeconds(2));
        TickAt(2);
        _hw.CpuTempC = 81;
        var s = TickAt(3);

        Assert.Equal(EcTrialProblem.TooHot, s.Trial!.Problem);
        Assert.Null(s.Trial.WmiFollows);
        Assert.True(_hw.WmiWrites[^1].Target.IsAuto); // handed back the way it was written
        Assert.Equal(FanWritePath.Ec, _hw.WritePath);
    }

    [Fact]
    public void A_wmi_version_is_written_through_wmi_and_never_below_30()
    {
        var hw = new FakeFanHardware { EcVersion = "P010A0", WmiWorks = true, CpuTempC = 40, GpuTempC = 38 };
        var loop = new FanControlLoop(hw, new FanLoopOptions
        {
            KnownEcVersions = new HashSet<string> { "P010A0" },
            WmiEcVersions = new HashSet<string> { "P010A0" },
        });
        loop.Start(FanProfile.Quiet, T0);
        for (int i = 1; i <= 20; i++) loop.Tick(T0.AddSeconds(i));

        Assert.Equal(FanWritePath.Wmi, loop.Status.WritePath);
        Assert.Empty(hw.Writes);
        Assert.Contains(hw.WmiWrites, w => !w.Target.IsAuto);
        Assert.All(hw.WmiWrites.Where(w => !w.Target.IsAuto), w => Assert.True(w.Target.Percent >= FanControlLoop.WmiFloor));
    }

    [Fact]
    public void A_version_trusted_on_the_ec_is_written_on_the_ec()
    {
        var hw = new FakeFanHardware { EcVersion = "P010A0", WmiWorks = true };
        var loop = new FanControlLoop(hw, new FanLoopOptions { KnownEcVersions = new HashSet<string> { "P010A0" } });
        loop.Start(FanProfile.Balanced, T0);
        Assert.Equal(FanWritePath.Ec, hw.WritePath);
    }

    [Fact]
    public void A_warm_machine_does_not_start_a_trial()
    {
        _hw.CpuTempC = 76;
        TickAt(2);
        int writes = _hw.Writes.Count;
        _loop.StartEcTrial(T0.AddSeconds(3));
        TickAt(3);

        Assert.Equal(EcTrialState.Failed, _loop.Status.Trial!.State);
        Assert.Equal(EcTrialProblem.TooHot, _loop.Status.Trial.Problem);
        Assert.Equal(writes, _hw.Writes.Count);
    }

    [Fact]
    public void Getting_hot_during_the_trial_stops_it()
    {
        _loop.StartEcTrial(T0.AddSeconds(2));
        TickAt(2);
        _hw.CpuTempC = 81;
        var s = TickAt(3);

        Assert.Equal(EcTrialProblem.TooHot, s.Trial!.Problem);
        Assert.True(_hw.CpuTarget.IsAuto);
    }

    [Fact]
    public void Stopping_the_service_during_the_trial_hands_the_fans_back()
    {
        _hw.WmiWorks = true;
        _loop.StartEcTrial(T0.AddSeconds(2));
        TickAt(2);
        Assert.False(_hw.CpuTarget.IsAuto);
        _loop.Stop(T0.AddSeconds(3));

        Assert.Equal(EcTrialProblem.Stopped, _loop.Status.Trial!.Problem);
        Assert.True(_hw.CpuTarget.IsAuto);
        Assert.True(_hw.GpuTarget.IsAuto);
        Assert.False(_loop.Status.HoldsFans);
    }

    [Fact]
    public void A_verified_ec_has_no_trial()
    {
        var hw = new FakeFanHardware();
        var loop = new FanControlLoop(hw);
        loop.Start(FanProfile.Balanced, T0);
        loop.Tick(T0.AddSeconds(1));
        Assert.Throws<InvalidOperationException>(() => loop.StartEcTrial(T0.AddSeconds(2)));
    }

    [Fact]
    public void A_trusted_version_from_the_settings_runs_at_start()
    {
        var hw = new FakeFanHardware { EcVersion = "P010A0" };
        var loop = new FanControlLoop(hw, new FanLoopOptions { KnownEcVersions = new HashSet<string> { "P008B0", "P010A0" } });
        loop.Start(FanProfile.Balanced, T0);
        Assert.Equal(FanLoopMode.Running, loop.Status.Mode);
    }
}
