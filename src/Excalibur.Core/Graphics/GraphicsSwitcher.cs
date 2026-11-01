using Excalibur.Core.Hardware;

namespace Excalibur.Core.Graphics;

/// <summary>A graphics mode change that could not be made; <see cref="Code"/> is stable for the UI to word.</summary>
public sealed class GraphicsException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>The NVIDIA GPU and its platform framework device (ACPI\NVDA0820) in Windows' device manager.</summary>
public interface IGpuDevices
{
    /// <summary>True when the NVIDIA GPU is enabled, false when switched off, null when there is none.</summary>
    bool? Enabled { get; }
    /// <summary>Switches the GPU off, then the framework device; throws <see cref="GraphicsException"/>.</summary>
    void Disable();
    /// <summary>Switches the GPU on, then the framework device; throws <see cref="GraphicsException"/>.</summary>
    void Enable();
}

/// <summary>Writes the graphics mode (SET 0x0203: 1 Hybrid, 2 Discrete, 3 Integrated → CMOS D5/D6; 3 also sets the EC's GFXT to 0).</summary>
public sealed class GraphicsModeWriter(ISmiChannel firmware)
{
    /// <summary>Throws <see cref="GraphicsException"/> (graphics_firmware) when the firmware call fails.</summary>
    public void Set(GraphicsMode mode)
    {
        var command = SmiCommand.Set(GraphicsModeReader.Function);
        command.A2 = (uint)mode;
        try { firmware.Execute(command); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException
#if !LINUX
            or System.Management.ManagementException
#endif
            )
        {
            throw new GraphicsException("graphics_firmware", $"The BIOS call failed: {ex.Message}", ex);
        }
    }
}

/// <summary>Where a change stands: running, or how the last one ended.</summary>
public sealed record GraphicsSwitchState(bool Busy, GraphicsMode? Target, string? ErrorCode = null, string? Error = null,
    bool RestartAdvised = false);

/// <summary>
/// Changes between Hybrid and Integrated without a restart, the way the stock Control Center does on this model (SKU 49,
/// "old VGA mode"; .scratch/ekran-karti-modu/spec.md). Integrated: refused with an HDMI cable in (the port is wired to the
/// NVIDIA GPU; the cable is read fresh, the BIOS raises no event when it comes out) or after a Discrete boot; the GPU's
/// clock lock and NVML are let go (<paramref name="releaseGpu"/>: a handle left open crashed the service, 2026-10-23),
/// the GPU and its framework device are switched off, then the BIOS is told. Hybrid: devices on, BIOS told, NVML may open
/// again (<paramref name="resumeGpu"/>); a sleep or a restart is advised until the next wake (<see cref="Woke"/>): the EC's
/// GFXT stays 0 after Integrated (the BIOS sets it to 1 only at boot and on waking), so the EC itself does not see the GPU
/// temperature (our fans take it from the NVIDIA driver meanwhile). The driver's power limit came back at once on
/// 2026-11-02 (85 W); on 2026-10-23, with NVML left open, it had not. A failed step puts back what was done.
/// </summary>
public sealed class GraphicsSwitcher(GraphicsModeMonitor monitor, GraphicsModeWriter writer, IGpuDevices devices,
    Action releaseGpu, Action resumeGpu, Action<string> log)
{
    private readonly object _gate = new();
    private GraphicsSwitchState _state = new(false, null);
    private bool _restartAdvised;

    public GraphicsSwitchState State { get { lock (_gate) return _state; } }

    /// <summary>
    /// Starts a change in the background and returns at once (the switch takes several seconds); null when started, else
    /// the refusal. The outcome shows in <see cref="State"/>.
    /// </summary>
    public GraphicsSwitchState? Start(GraphicsMode target, string source)
    {
        lock (_gate)
        {
            if (_state.Busy) return new GraphicsSwitchState(true, _state.Target, "graphics_busy", "A graphics mode change is running.");
            if (target == GraphicsMode.Discrete)
                return new GraphicsSwitchState(false, target, "graphics_unsupported", "Discrete is not available yet.");
            _state = new GraphicsSwitchState(true, target, RestartAdvised: _restartAdvised);
        }
        Task.Run(() => Switch(target, source));
        return null;
    }

    /// <summary>Makes the change now (on the caller's thread) and records the outcome in <see cref="State"/>.</summary>
    public void Switch(GraphicsMode target, string source)
    {
        string? code = null, error = null;
        try
        {
            if (target == GraphicsMode.Integrated) ToIntegrated(source);
            else if (target == GraphicsMode.Hybrid) ToHybrid(source);
            else throw new GraphicsException("graphics_unsupported", "Discrete is not available yet.");
        }
        catch (GraphicsException ex)
        {
            (code, error) = (ex.Code, ex.Message);
            log($"graphics: {target} not set ({source}): {ex.Message}");
        }
        catch (Exception ex)
        {
            // Never leave the state busy; the log keeps the details.
            (code, error) = ("graphics_failed", ex.Message);
            log($"graphics: {target} failed ({source}): {ex}");
        }
        lock (_gate) _state = new GraphicsSwitchState(false, target, code, error, _restartAdvised);
    }

    /// <summary>After a wake: the BIOS has set GFXT back to 1, so the advice ends (once Hybrid).</summary>
    public void Woke()
    {
        lock (_gate)
        {
            if (!_restartAdvised || _state.Busy || monitor.Current?.Mode != GraphicsMode.Hybrid) return;
            _restartAdvised = false;
            _state = _state with { RestartAdvised = false };
        }
    }

    private GraphicsReading Fresh(string source)
    {
        monitor.Refresh(source);
        return monitor.Current ?? throw new GraphicsException("graphics_unsupported", "This BIOS does not report the graphics mode.");
    }

    private void ToIntegrated(string source)
    {
        var now = Fresh(source);
        if (now.Mode == GraphicsMode.Integrated) return;
        if (now.HdmiConnected) throw new GraphicsException("graphics_hdmi", "An HDMI cable is in (the port is on the NVIDIA GPU).");
        if (now.Mode == GraphicsMode.Discrete)
            throw new GraphicsException("graphics_discrete", "Started in Discrete: choose Hybrid and restart first.");

        log($"graphics: switching to Integrated ({source})");
        releaseGpu();
        try { devices.Disable(); }
        catch (GraphicsException)
        {
            TryEnable();
            resumeGpu();
            throw;
        }
        bool taken;
        try
        {
            writer.Set(GraphicsMode.Integrated);
            taken = Fresh(source).Mode == GraphicsMode.Integrated;
        }
        catch (GraphicsException) { taken = false; }
        if (!taken)
        {
            TryEnable();
            try { writer.Set(GraphicsMode.Hybrid); }
            catch (GraphicsException ex) { log($"graphics: Hybrid could not be written back: {ex.Message}"); }
            resumeGpu();
            throw new GraphicsException("graphics_not_set", "The BIOS did not take Integrated; the NVIDIA GPU is back on.");
        }
        log("graphics: Integrated (NVIDIA GPU off)");
    }

    private void ToHybrid(string source)
    {
        var now = Fresh(source);
        if (now.Mode == GraphicsMode.Hybrid && devices.Enabled != false) return;
        if (now.Mode == GraphicsMode.Discrete)
            throw new GraphicsException("graphics_unsupported", "Leaving Discrete needs a restart (not available yet).");

        log($"graphics: switching to Hybrid ({source})");
        devices.Enable();
        resumeGpu();
        lock (_gate) _restartAdvised = true;
        writer.Set(GraphicsMode.Hybrid);
        var after = Fresh(source);
        log(after.Mode == GraphicsMode.Hybrid ? "graphics: Hybrid (NVIDIA GPU on; a sleep or restart is advised)" : $"graphics: the BIOS still reports {after.Mode}");
    }

    private void TryEnable()
    {
        try { devices.Enable(); }
        catch (GraphicsException ex) { log($"graphics: the NVIDIA GPU could not be switched back on: {ex.Message}"); }
    }
}
