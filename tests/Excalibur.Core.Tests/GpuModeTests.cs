using Excalibur.Core.Gpu;
using Excalibur.Core.Settings;

namespace Excalibur.Core.Tests;

public sealed class GpuModeTests : IDisposable
{
    /// <summary>Models the BIOS: the mode a start runs in is what was written before it (Integrated also at once).</summary>
    private sealed class FakeGpu : IGpuHardware
    {
        public GpuMode Running = GpuMode.Hybrid, Next = GpuMode.Hybrid;
        public bool Hdmi, NvidiaOn = true;
        public bool? BitLocker = true;
        public List<GpuMode> Written { get; } = [];

        public (GpuMode? Mode, bool Hdmi) Read() => (Running, Hdmi);

        public void Write(GpuMode mode)
        {
            Written.Add(mode);
            Next = mode;
            if (mode == GpuMode.Integrated) Running = GpuMode.Integrated;
            else if (Running == GpuMode.Integrated && mode == GpuMode.Hybrid) Running = GpuMode.Hybrid;
        }

        public bool SetNvidiaEnabled(bool enabled) { NvidiaOn = enabled; return false; }
        public bool? BitLockerOff() => BitLocker;

        public void Restart() => Running = Next == GpuMode.Integrated ? GpuMode.Hybrid : Next;
    }

    private readonly string _path = Path.Combine(Path.GetTempPath(), "excalibur-gpu-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly FakeGpu _hw = new();
    private readonly ServiceSettingsStore _store;
    private GpuModeManager _m;

    public GpuModeTests()
    {
        _store = new ServiceSettingsStore(_path);
        _m = new GpuModeManager(_hw, _store);
        _m.Start();
    }

    public void Dispose() => File.Delete(_path);

    /// <summary>A restart: the BIOS applies the stored mode, the service starts again.</summary>
    private void Restart()
    {
        _hw.Restart();
        _m = new GpuModeManager(_hw, _store);
        _m.Start();
    }

    [Fact]
    public void Integrated_turns_the_nvidia_card_off_at_once_and_hybrid_turns_it_back_on()
    {
        Assert.True(_m.Set(GpuMode.Integrated) is { Ok: true, RestartNeeded: false });
        Assert.False(_hw.NvidiaOn);
        Assert.Equal(GpuMode.Integrated, _m.Status.Mode);
        Assert.True(_m.Set(GpuMode.Hybrid) is { Ok: true, RestartNeeded: false });
        Assert.True(_hw.NvidiaOn);
        Assert.Equal(GpuMode.Hybrid, _m.Status.Mode);
    }

    [Fact]
    public void Integrated_is_refused_with_an_external_display()
    {
        _hw.Hdmi = true;
        Assert.Equal("gpu_hdmi", _m.Set(GpuMode.Integrated).Code);
        Assert.True(_hw.NvidiaOn);
    }

    [Fact]
    public void An_external_display_in_integrated_goes_back_to_hybrid()
    {
        _m.Set(GpuMode.Integrated);
        _hw.Hdmi = true;
        Assert.True(_m.OnHdmi());
        Assert.True(_hw.NvidiaOn);
        Assert.Equal(GpuMode.Hybrid, _m.Status.Mode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Discrete_is_refused_unless_bitlocker_is_surely_off(bool? off)
    {
        _hw.BitLocker = off;
        Assert.Equal("gpu_bitlocker", _m.Set(GpuMode.Discrete).Code);
        Assert.Empty(_hw.Written);
    }

    [Fact]
    public void Discrete_needs_a_restart_and_then_writes_hybrid_back_until_confirmed()
    {
        Assert.True(_m.Set(GpuMode.Discrete) is { Ok: true, RestartNeeded: true });
        Assert.Equal(GpuMode.Discrete, _m.Status.Pending);

        Restart();
        Assert.Equal(GpuMode.Discrete, _m.Status.Mode);
        Assert.Equal(GpuMode.Hybrid, _hw.Next); // a black screen costs one forced power-off
        Assert.Equal(GpuTrial.AwaitingConfirm, _m.Status.Trial);

        Assert.True(_m.Confirm().Ok);
        Assert.Equal(GpuMode.Discrete, _hw.Next);
        Assert.Equal(GpuTrial.None, _m.Status.Trial);
        Restart();
        Assert.Equal(GpuMode.Discrete, _m.Status.Mode);
        Assert.Equal(GpuMode.Discrete, _hw.Next);
    }

    [Fact]
    public void An_unconfirmed_discrete_start_comes_back_in_hybrid_and_ends_the_trial()
    {
        _m.Set(GpuMode.Discrete);
        Restart(); // black screen: the user holds the power button
        Restart();
        Assert.Equal(GpuMode.Hybrid, _m.Status.Mode);
        Assert.Equal(GpuTrial.None, _m.Status.Trial);
        Assert.Null(_m.Status.Pending);
    }

    [Fact]
    public void From_discrete_integrated_is_refused_and_hybrid_needs_a_restart()
    {
        _m.Set(GpuMode.Discrete);
        Restart();
        _m.Confirm();
        Assert.Equal("gpu_from_discrete", _m.Set(GpuMode.Integrated).Code);
        Assert.True(_m.Set(GpuMode.Hybrid) is { Ok: true, RestartNeeded: true });
        Restart();
        Assert.Equal(GpuMode.Hybrid, _m.Status.Mode);
        Assert.Null(_m.Status.Pending);
    }

    [Fact]
    public void Discrete_from_integrated_turns_the_card_on_first()
    {
        _m.Set(GpuMode.Integrated);
        _m.Set(GpuMode.Discrete);
        Assert.True(_hw.NvidiaOn);
    }
}
