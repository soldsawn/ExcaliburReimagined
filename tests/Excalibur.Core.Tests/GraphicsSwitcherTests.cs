using Excalibur.Core.Graphics;
using Excalibur.Core.Hardware;

namespace Excalibur.Core.Tests;

public class GraphicsSwitcherTests
{
    private readonly SimulatedSmiChannel _bios = new();
    private readonly SimulatedGpuDevices _gpu = new();
    private readonly List<string> _log = [];
    private readonly List<string> _calls = [];
    private bool _bitLocker;
    private int _restarts;
    private readonly GraphicsModeMonitor _monitor;
    private readonly GraphicsSwitcher _switcher;

    public GraphicsSwitcherTests()
    {
        _monitor = new GraphicsModeMonitor(new GraphicsModeReader(_bios).Read, _log.Add);
        _switcher = new GraphicsSwitcher(_monitor, new GraphicsModeWriter(_bios), _gpu,
            () => _calls.Add("release"), () => _calls.Add("resume"), _log.Add, () => _bitLocker, () => _restarts++);
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
    public void Discrete_is_told_to_the_BIOS_then_Windows_restarts()
    {
        _switcher.Switch(GraphicsMode.Discrete, "window");
        Assert.Equal(1, _restarts);
        Assert.Equal(GraphicsMode.Discrete, _bios.LastSet);
        Assert.Equal(GraphicsMode.Hybrid, _bios.Mode); // the boot mode changes with the restart
        Assert.True(_switcher.State.RestartPending);
        Assert.Null(_switcher.State.ErrorCode);
    }

    [Fact]
    public void Discrete_is_refused_under_BitLocker()
    {
        _bitLocker = true;
        _switcher.Switch(GraphicsMode.Discrete, "window");
        Assert.Equal("graphics_bitlocker", _switcher.State.ErrorCode);
        Assert.Equal(0, _restarts);
        Assert.Null(_bios.LastSet);
    }

    [Fact]
    public void Discrete_from_Integrated_switches_the_GPU_back_on_first()
    {
        _switcher.Switch(GraphicsMode.Integrated, "window");
        _calls.Clear();
        _switcher.Switch(GraphicsMode.Discrete, "window");
        Assert.True(_gpu.Enabled);
        Assert.Equal(["resume"], _calls);
        Assert.Equal(1, _restarts);
        Assert.Equal(GraphicsMode.Hybrid, _monitor.Current!.Mode); // until the restart
        _switcher.Switch(GraphicsMode.Integrated, "window");
        Assert.Equal("graphics_restart_pending", _switcher.State.ErrorCode);
        Assert.True(_gpu.Enabled);
    }

    [Fact]
    public void Hybrid_before_the_restart_takes_Discrete_back()
    {
        _switcher.Switch(GraphicsMode.Discrete, "window");
        _switcher.Switch(GraphicsMode.Hybrid, "window");
        Assert.Equal(GraphicsMode.Hybrid, _bios.LastSet);
        Assert.False(_switcher.State.RestartPending);
        Assert.Equal(1, _restarts);
    }

    [Fact]
    public void Leaving_a_Discrete_boot_is_Hybrid_and_a_restart_and_Integrated_is_refused()
    {
        _bios.Mode = GraphicsMode.Discrete;
        _switcher.Switch(GraphicsMode.Integrated, "window");
        Assert.Equal("graphics_discrete", _switcher.State.ErrorCode);
        _switcher.Switch(GraphicsMode.Hybrid, "window");
        Assert.Equal(GraphicsMode.Hybrid, _bios.LastSet);
        Assert.Equal(1, _restarts);
        Assert.True(_switcher.State.RestartPending);
        _switcher.Switch(GraphicsMode.Discrete, "window"); // changed their mind before the restart
        Assert.Equal(GraphicsMode.Discrete, _bios.LastSet);
        Assert.False(_switcher.State.RestartPending);
        Assert.Equal(1, _restarts);
    }

    [Fact]
    public void A_second_change_waits_for_the_first()
    {
        Assert.Null(_switcher.Start(GraphicsMode.Integrated, "window"));
        // Busy until the background switch ends (it may already have ended on a fast machine).
        var second = _switcher.Start(GraphicsMode.Hybrid, "window");
        Assert.True(second is null || second.ErrorCode == "graphics_busy");
    }

    [Fact]
    public void Integrated_is_refused_where_it_is_not_offered_and_nothing_is_touched()
    {
        var linux = new GraphicsSwitcher(_monitor, new GraphicsModeWriter(_bios), _gpu,
            () => _calls.Add("release"), () => _calls.Add("resume"), _log.Add, () => false, () => _restarts++, integrated: false);
        Assert.Equal("graphics_unsupported", linux.Start(GraphicsMode.Integrated, "window")?.ErrorCode);
        Assert.Empty(_calls);
        Assert.Null(_bios.LastSet);
        Assert.Null(linux.Start(GraphicsMode.Discrete, "window")); // Discrete is still offered
    }
}
