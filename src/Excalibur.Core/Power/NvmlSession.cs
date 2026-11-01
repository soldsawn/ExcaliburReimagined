namespace Excalibur.Core.Power;

/// <summary>The few NVML calls that open and close it (the readings are <see cref="NvmlCall"/>s on the device handle).</summary>
public interface INvmlApi
{
    int Init();
    int Shutdown();
    int GetHandle(out IntPtr device);
}

/// <summary>One NVML reading or command on the device handle: NVML's return code (0 = done) and its value.</summary>
public delegate int NvmlCall(IntPtr device, out uint value);

/// <summary>
/// NVML opened once and kept, until <see cref="Release"/>: a handle left open while the NVIDIA GPU is switched off crashed
/// the service with an access violation inside nvml.dll (2026-10-23 20:04, the graphics mode trial), which no catch can
/// stop. Released, nothing is called until <see cref="Resume"/>. A GPU NVML reports lost drops the handle too, and a start
/// that failed (driver not loaded) is tried again after <see cref="RetryMs"/>. Watts are not believed for
/// <see cref="SettleMs"/> after resuming (61 and 590 W read while the card woke up) nor above <see cref="MaxMilliwatts"/>.
/// </summary>
public sealed class NvmlSession(INvmlApi api, Func<long> nowMs)
{
    public const int ErrorUninitialized = 1, ErrorGpuIsLost = 15;
    public const long RetryMs = 30_000, SettleMs = 60_000;
    public const uint MaxMilliwatts = 250_000;

    private readonly object _gate = new();
    private bool _ready, _missing;
    private long _nextTry = long.MinValue, _settledAt = long.MinValue;
    private IntPtr _device;

    /// <summary>True between <see cref="Release"/> and <see cref="Resume"/>.</summary>
    public bool Released { get; private set; }

    /// <summary>Closes NVML (before the GPU is switched off); no NVML call is made until <see cref="Resume"/>.</summary>
    public void Release()
    {
        lock (_gate)
        {
            Released = true;
            Close();
        }
    }

    /// <summary>NVML may be opened again (the GPU is back); watts wait <see cref="SettleMs"/>.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (!Released) return;
            Released = false;
            _nextTry = long.MinValue;
            _settledAt = nowMs() + SettleMs;
        }
    }

    /// <summary>The call's value, or null when NVML is released, missing, not started or the call failed.</summary>
    public int? Query(NvmlCall call)
    {
        lock (_gate)
        {
            if (Released || _missing || !Open()) return null;
            int code;
            uint value;
            try { code = call(_device, out value); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
            if (code is ErrorGpuIsLost or ErrorUninitialized)
            {
                Close();
                _nextTry = nowMs() + RetryMs;
                return null;
            }
            return code == 0 ? (int)value : null;
        }
    }

    /// <summary>A power reading in whole watts; null while settling after a resume or when absurd.</summary>
    public int? Milliwatts(NvmlCall call)
    {
        if (nowMs() < Volatile.Read(ref _settledAt)) return null;
        return Query(call) is { } mw && mw <= MaxMilliwatts ? (int)Math.Round(mw / 1000.0) : null;
    }

    private bool Open()
    {
        if (_ready) return true;
        if (nowMs() < _nextTry) return false;
        try
        {
            _ready = api.Init() == 0 && api.GetHandle(out _device) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _missing = true; // no NVIDIA driver here: never again
            return false;
        }
        if (!_ready) _nextTry = nowMs() + RetryMs;
        return _ready;
    }

    private void Close()
    {
        if (!_ready) return;
        _ready = false;
        _device = IntPtr.Zero;
        try { api.Shutdown(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
}
