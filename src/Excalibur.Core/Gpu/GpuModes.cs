using System.Text.Json.Serialization;
using Excalibur.Core.Settings;

namespace Excalibur.Core.Gpu;

/// <summary>
/// The graphics mode, as WMI 0x0203 takes it (docs/02). On this board (SKU 49, no MUX: 0x0202 a6 = 1) the BIOS stores
/// the choice in CMOS (0xD5/0xD6) and applies it at the next start (DSDT WSAA/WQAA):
/// <list type="bullet">
///   <item>Hybrid: Optimus, the BIOS default.</item>
///   <item>Discrete: the BIOS starts with the NVIDIA card as the only one. Needs a restart; untried, and the panel is
///   wired to the Intel graphics, so it may start with a black screen: see <see cref="GpuTrial"/>.</item>
///   <item>Integrated: the BIOS still starts Hybrid (it only notes 0x55); like the stock software (old VGA mode for
///   SKU 49) the NVIDIA card and its platform framework are turned off in Windows (PnP). No restart.</item>
/// </list>
/// </summary>
public enum GpuMode
{
    Hybrid = 1,
    Discrete = 2,
    Integrated = 3,
}

/// <summary>
/// The safety net for Discrete: on the first start in that mode the service writes Hybrid back at once, so a black
/// screen needs only a forced power-off (the next start is Hybrid). The user keeps the mode with confirmGpuMode.
/// </summary>
public enum GpuTrial
{
    None = 0,
    /// <summary>Discrete was asked for; the restart has not happened yet.</summary>
    AwaitingRestart = 1,
    /// <summary>Started in Discrete; Hybrid is written for the next start until the user confirms.</summary>
    AwaitingConfirm = 2,
}

/// <summary>What the service can do about the graphics mode.</summary>
public interface IGpuHardware
{
    /// <summary>GET 0x0203: the mode this start runs in, and whether an external display (HDMI) is connected.</summary>
    (GpuMode? Mode, bool Hdmi) Read();

    /// <summary>SET 0x0203: the mode for the next start (Integrated also takes effect now, with the devices turned off).</summary>
    void Write(GpuMode mode);

    /// <summary>Turns the NVIDIA card and its platform framework on or off in Windows; throws <see cref="GpuException"/>.</summary>
    void SetNvidiaEnabled(bool enabled);

    /// <summary>True when drive C: is not protected by BitLocker, false when it is, null when that cannot be told.</summary>
    bool? BitLockerOff();
}

public sealed class GpuException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The graphics mode as the apps show it. <see cref="Pending"/> is what the next restart brings, null when nothing changes.</summary>
public sealed record GpuStatusDto(GpuMode? Mode, GpuMode? Pending, bool Hdmi, GpuTrial Trial)
{
    [JsonIgnore]
    public bool RestartNeeded => Pending is { } p && p != Mode;
}

/// <summary>What a mode request ended up as; <see cref="Code"/> is stable, for the UI to word.</summary>
public sealed record GpuResult(bool Ok, string? Code = null, string? Message = null, bool RestartNeeded = false)
{
    public static GpuResult Fail(string code, string message) => new(false, code, message);
}

/// <summary>
/// The service's graphics mode logic (docs/09 A7). Every firmware and device call is guarded; a failure is logged and
/// reported, never thrown at the caller. Thread-safe.
/// </summary>
public sealed class GpuModeManager(IGpuHardware hw, ServiceSettingsStore settings, Action<string>? log = null)
{
    private readonly object _gate = new();
    private GpuMode? _mode;
    private bool _hdmi;

    public GpuStatusDto Status
    {
        get
        {
            lock (_gate)
            {
                var s = settings.Current;
                return new GpuStatusDto(_mode, s.GpuPending is { } p && p != _mode ? p : null, _hdmi, s.GpuTrial);
            }
        }
    }

    /// <summary>At service start: reads the mode and runs the Discrete safety net.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (!Refresh()) return;
            var s = settings.Current;
            if (s.GpuPending == _mode) Store(s with { GpuPending = null });
            switch (settings.Current.GpuTrial)
            {
                case GpuTrial.AwaitingRestart when _mode == GpuMode.Discrete:
                    // First start in Discrete: the next one is Hybrid unless the user confirms that this one works.
                    if (TryWrite(GpuMode.Hybrid))
                    {
                        Store(settings.Current with { GpuTrial = GpuTrial.AwaitingConfirm });
                        log?.Invoke("gpu: started in Discrete; Hybrid written for the next start until confirmed");
                    }
                    break;
                case GpuTrial.AwaitingConfirm when _mode != GpuMode.Discrete:
                    // The safety net did its job (or the user went back): the trial is over.
                    Store(settings.Current with { GpuTrial = GpuTrial.None, GpuPending = null });
                    log?.Invoke("gpu: Discrete was not confirmed; back in Hybrid");
                    break;
                case GpuTrial.AwaitingConfirm:
                    // Still Discrete after a restart without confirming: keep Hybrid for the next start.
                    TryWrite(GpuMode.Hybrid);
                    break;
            }
        }
    }

    /// <summary>The user picked a mode.</summary>
    public GpuResult Set(GpuMode mode)
    {
        if (!Enum.IsDefined(mode)) return GpuResult.Fail("gpu_failed", $"unknown graphics mode {(int)mode}");
        lock (_gate)
        {
            if (!Refresh()) return GpuResult.Fail("gpu_unavailable", "the graphics mode cannot be read on this machine");
            var s = settings.Current;
            if (mode == _mode && s.GpuPending is null && s.GpuTrial == GpuTrial.None) return new GpuResult(true);
            log?.Invoke($"gpu: {mode} asked (now {_mode})");
            try
            {
                switch (mode)
                {
                    case GpuMode.Integrated:
                        if (_hdmi) return GpuResult.Fail("gpu_hdmi", "an external display is connected to the NVIDIA card");
                        if (_mode == GpuMode.Discrete) return GpuResult.Fail("gpu_from_discrete", "switch to Hybrid and restart first");
                        hw.SetNvidiaEnabled(false);
                        hw.Write(GpuMode.Integrated);
                        Store(s with { GpuPending = null, GpuTrial = GpuTrial.None });
                        Refresh();
                        return new GpuResult(true);
                    case GpuMode.Hybrid:
                        if (_mode == GpuMode.Integrated) hw.SetNvidiaEnabled(true);
                        hw.Write(GpuMode.Hybrid);
                        bool restart = _mode == GpuMode.Discrete;
                        // From Discrete the trial ends here: the user chose Hybrid for the next start.
                        Store(s with { GpuPending = restart ? GpuMode.Hybrid : null, GpuTrial = GpuTrial.None });
                        Refresh();
                        return new GpuResult(true, RestartNeeded: restart);
                    default:
                        if (_mode == GpuMode.Discrete && s.GpuTrial == GpuTrial.AwaitingConfirm) return Confirm();
                        if (hw.BitLockerOff() != true)
                            return GpuResult.Fail("gpu_bitlocker", "drive C: is (or may be) protected by BitLocker");
                        if (_mode == GpuMode.Integrated) hw.SetNvidiaEnabled(true);
                        hw.Write(GpuMode.Discrete);
                        Store(s with { GpuPending = GpuMode.Discrete, GpuTrial = GpuTrial.AwaitingRestart });
                        return new GpuResult(true, RestartNeeded: true);
                }
            }
            catch (Exception ex) when (ex is GpuException or InvalidOperationException or System.Management.ManagementException)
            {
                log?.Invoke($"gpu: {mode} failed ({ex.Message})");
                return GpuResult.Fail("gpu_failed", ex.Message);
            }
        }
    }

    /// <summary>The user says Discrete works: keep it for the next starts too.</summary>
    public GpuResult Confirm()
    {
        lock (_gate)
        {
            if (settings.Current.GpuTrial != GpuTrial.AwaitingConfirm || _mode != GpuMode.Discrete)
                return GpuResult.Fail("gpu_no_trial", "there is no graphics mode to confirm");
            if (!TryWrite(GpuMode.Discrete)) return GpuResult.Fail("gpu_failed", "the mode could not be written");
            Store(settings.Current with { GpuTrial = GpuTrial.None, GpuPending = null });
            log?.Invoke("gpu: Discrete confirmed");
            return new GpuResult(true);
        }
    }

    /// <summary>An external display came or went (firmware event 4): Integrated cannot drive it, so go back to Hybrid.</summary>
    public bool OnHdmi()
    {
        lock (_gate)
        {
            if (!Refresh() || !_hdmi || _mode != GpuMode.Integrated) return false;
            log?.Invoke("gpu: external display in Integrated; back to Hybrid");
        }
        return Set(GpuMode.Hybrid).Ok;
    }

    private bool Refresh()
    {
        try
        {
            (_mode, _hdmi) = hw.Read();
            return _mode is not null;
        }
        catch (Exception ex) when (ex is GpuException or InvalidOperationException or System.Management.ManagementException)
        {
            log?.Invoke($"gpu: mode unreadable ({ex.Message})");
            return false;
        }
    }

    private bool TryWrite(GpuMode mode)
    {
        try { hw.Write(mode); return true; }
        catch (Exception ex) when (ex is GpuException or InvalidOperationException or System.Management.ManagementException)
        {
            log?.Invoke($"gpu: writing {mode} failed ({ex.Message})");
            return false;
        }
    }

    private void Store(ServiceSettings next) => settings.Update(_ => next, out _);
}

/// <summary>The --simulate stand-in: remembers the mode like the BIOS would, touches nothing.</summary>
public sealed class SimulatedGpuHardware : IGpuHardware
{
    private GpuMode _mode = GpuMode.Hybrid;
    public (GpuMode? Mode, bool Hdmi) Read() => (_mode, false);
    public void Write(GpuMode mode) { if (mode == GpuMode.Integrated || _mode == GpuMode.Integrated) _mode = mode; }
    public void SetNvidiaEnabled(bool enabled) { }
    public bool? BitLockerOff() => true;
}
