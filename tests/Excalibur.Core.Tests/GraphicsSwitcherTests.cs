using Excalibur.Core.Graphics;
using Excalibur.Core.Hardware;

namespace Excalibur.Core.Tests;

public class GraphicsSwitcherTests
{
    private readonly SimulatedSmiChannel _bios = new();
    private readonly SimulatedGpuDevices _gpu = new();
    private readonly List<string> _log = [];
    private readonly List<string> _calls = [];
    private readonly GraphicsModeMonitor _monitor;
    private readonly GraphicsSwitcher _switcher;

    public GraphicsSwitcherTests()
    {
        _monitor = new GraphicsModeMonitor(new GraphicsModeReader(_bios).Read, _log.Add);
        _switcher = new GraphicsSwitcher(_monitor, new GraphicsModeWriter(_bios), _gpu,
            () => _calls.Add("release"), () => _calls.Add("resume"), _log.Add);
    }

    [Fact]
    public void Integrated_lets_NVML_go_before_the_GPU_switches_off_then_tells_the_BIOS()
    {
        _switcher.Switch(GraphicsMode.Integrated, "window");
        Assert.Equal(["release"], _calls);
        Assert.False(_gpu.Enabled);
        Assert.Equal(GraphicsMode.Integrated, _bios.Mode);
        Assert.Equal(new GraphicsSwitchState(false, GraphicsMode.Integrated), _switcher.State);
    }

    [Fact]
    public void Integrated_is_refused_with_an_HDMI_cable_in_read_fresh()
    {
        _monitor.Refresh("start"); // cable out at start
        _bios.HdmiConnected = true; // in later: the BIOS raises no event we would have seen here
        _switcher.Switch(GraphicsMode.Integrated, "window");
        Assert.Equal("graphics_hdmi", _switcher.State.ErrorCode);
        Assert.Empty(_calls);
        Assert.Equal(0, _gpu.Disables);
        Assert.Equal(GraphicsMode.Hybrid, _bios.Mode);
    }

    [Fact]
    public void Integrated_is_refused_after_a_Discrete_boot()
    {
        _bios.Mode = GraphicsMode.Discrete;
        _switcher.Switch(GraphicsMode.Integrated, "window");
        Assert.Equal("graphics_discrete", _switcher.State.ErrorCode);
        Assert.Equal(0, _gpu.Disables);
    }

    [Fact]
    public void A_GPU_that_will_not_switch_off_is_put_back_and_the_BIOS_is_untouched()
    {
        _gpu.FailWith = "pnputil said no";
        _switcher.Switch(GraphicsMode.Integrated, "window");
        Assert.Equal("graphics_devices", _switcher.State.ErrorCode);
        Assert.Equal(["release", "resume"], _calls);
        Assert.Equal(1, _gpu.Enables);
        Assert.Equal(GraphicsMode.Hybrid, _bios.Mode);
    }

    [Fact]
    public void Hybrid_switches_the_GPU_on_before_NVML_opens_and_advises_a_restart()
    {
        _switcher.Switch(GraphicsMode.Integrated, "window");
        _calls.Clear();
        _switcher.Switch(GraphicsMode.Hybrid, "HDMI");
        Assert.True(_gpu.Enabled);
        Assert.Equal(["resume"], _calls);
        Assert.Equal(GraphicsMode.Hybrid, _bios.Mode);
        Assert.True(_switcher.State.RestartAdvised);
        Assert.Null(_switcher.State.ErrorCode);
    }

    [Fact]
    public void A_wake_in_Hybrid_ends_the_advice()
    {
        _switcher.Switch(GraphicsMode.Integrated, "window");
        _switcher.Woke(); // still Integrated: nothing to end
        _switcher.Switch(GraphicsMode.Hybrid, "window");
        Assert.True(_switcher.State.RestartAdvised);
        _switcher.Woke();
        Assert.False(_switcher.State.RestartAdvised);
    }

    [Fact]
    public void Asking_for_the_mode_in_force_changes_nothing()
    {
        _switcher.Switch(GraphicsMode.Hybrid, "window");
        Assert.Empty(_calls);
        Assert.Equal(0, _gpu.Enables);
        Assert.False(_switcher.State.RestartAdvised);
    }

    [Fact]
    public void Discrete_is_not_offered_yet_and_a_second_change_waits_for_the_first()
    {
        Assert.Equal("graphics_unsupported", _switcher.Start(GraphicsMode.Discrete, "window")!.ErrorCode);
        Assert.Null(_switcher.Start(GraphicsMode.Integrated, "window"));
        // Busy until the background switch ends (it may already have ended on a fast machine).
        var second = _switcher.Start(GraphicsMode.Hybrid, "window");
        Assert.True(second is null || second.ErrorCode == "graphics_busy");
    }
}
