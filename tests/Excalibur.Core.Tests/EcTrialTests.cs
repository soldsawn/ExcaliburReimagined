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

    public EcTrialTests() : this(new FanLoopOptions()) { }

    private EcTrialTests(FanLoopOptions options)
    {
        _loop = new FanControlLoop(_hw, options);
        _loop.EcTrusted += (v, profile) => { _trusted.Add(v); _trustedProfile = profile; };
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
    public void It_waits_15_s_for_a_fan_before_failing()
    {
        _hw.DeafFan = FanId.Cpu;
        _loop.StartEcTrial(T0.AddSeconds(2));
        TickAt(2); // writes the CPU target
        for (int i = 3; i <= 16; i++) Assert.Equal(EcTrialState.Running, TickAt(i).Trial!.State);
        Assert.True(TickAt(16).HoldsFans);
        Assert.Equal(EcTrialState.Failed, TickAt(17).Trial!.State);
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
        _loop.StartEcTrial(T0.AddSeconds(2));
        TickAt(2);
        Assert.False(_hw.CpuTarget.IsAuto);
        _loop.Stop(T0.AddSeconds(3));

        Assert.Equal(EcTrialProblem.Stopped, _loop.Status.Trial!.Problem);
        Assert.True(_hw.CpuTarget.IsAuto);
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
