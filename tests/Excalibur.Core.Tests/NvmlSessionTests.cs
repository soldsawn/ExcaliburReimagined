using Excalibur.Core.Power;

namespace Excalibur.Core.Tests;

public class NvmlSessionTests
{
    private sealed class FakeNvml : INvmlApi
    {
        public int InitCode;
        public int Inits, Shutdowns;
        public int Handle = 7;
        public bool Up => Inits > Shutdowns;
        public int Init() { Inits++; return InitCode; }
        public int Shutdown() { Shutdowns++; return 0; }
        public int GetHandle(out IntPtr device) { device = Handle; return 0; }
    }

    private readonly FakeNvml _api = new();
    private long _now = 1_000_000;
    private readonly NvmlSession _session;

    public NvmlSessionTests() => _session = new NvmlSession(_api, () => _now);

    private static int Answer(IntPtr device, out uint value) { value = 42; return 0; }
    private static int Lost(IntPtr device, out uint value) { value = 0; return NvmlSession.ErrorGpuIsLost; }
    private static int NotSupported(IntPtr device, out uint value) { value = 0; return 3; }

    [Fact]
    public void Starts_once_and_answers()
    {
        Assert.Equal(42, _session.Query(Answer));
        Assert.Equal(42, _session.Query(Answer));
        Assert.Equal(1, _api.Inits);
    }

    [Fact]
    public void Released_it_shuts_NVML_down_and_makes_no_call_until_resumed()
    {
        _session.Query(Answer);
        _session.Release();
        Assert.False(_api.Up);
        Assert.True(_session.Released);
        bool called = false;
        Assert.Null(_session.Query((IntPtr d, out uint v) => { called = true; v = 1; return 0; }));
        Assert.False(called);
        Assert.Equal(1, _api.Inits);

        _session.Resume();
        Assert.Equal(42, _session.Query(Answer));
        Assert.Equal(2, _api.Inits);
    }

    [Fact]
    public void Release_before_the_first_call_never_starts_NVML()
    {
        _session.Release();
        Assert.Null(_session.Query(Answer));
        Assert.Equal(0, _api.Inits);
        Assert.Equal(0, _api.Shutdowns);
    }

    [Fact]
    public void A_lost_GPU_drops_the_handle_and_starts_again_later()
    {
        _session.Query(Answer);
        Assert.Null(_session.Query(Lost));
        Assert.False(_api.Up);
        Assert.Null(_session.Query(Answer)); // not at once
        _now += NvmlSession.RetryMs;
        Assert.Equal(42, _session.Query(Answer));
        Assert.Equal(2, _api.Inits);
    }

    [Fact]
    public void An_unsupported_reading_keeps_the_session()
    {
        _session.Query(Answer);
        Assert.Null(_session.Query(NotSupported));
        Assert.Equal(42, _session.Query(Answer));
        Assert.Equal(1, _api.Inits);
        Assert.Equal(0, _api.Shutdowns);
    }

    [Fact]
    public void A_failed_start_is_tried_again_after_a_while()
    {
        _api.InitCode = 9; // driver not loaded
        Assert.Null(_session.Query(Answer));
        Assert.Null(_session.Query(Answer));
        Assert.Equal(1, _api.Inits);
        _api.InitCode = 0;
        _now += NvmlSession.RetryMs;
        Assert.Equal(42, _session.Query(Answer));
    }

    [Fact]
    public void Watts_wait_a_minute_after_resuming_and_absurd_readings_are_dropped()
    {
        static int Mw(uint mw, out uint value) { value = mw; return 0; }
        Assert.Equal(85, _session.Milliwatts((IntPtr d, out uint v) => Mw(85_000, out v)));
        Assert.Null(_session.Milliwatts((IntPtr d, out uint v) => Mw(590_000, out v))); // read while the card woke up (2026-10-23)

        _session.Release();
        _session.Resume();
        Assert.Null(_session.Milliwatts((IntPtr d, out uint v) => Mw(85_000, out v)));
        Assert.Equal(42, _session.Query(Answer)); // other readings at once
        _now += NvmlSession.SettleMs;
        Assert.Equal(85, _session.Milliwatts((IntPtr d, out uint v) => Mw(85_000, out v)));
    }
}
