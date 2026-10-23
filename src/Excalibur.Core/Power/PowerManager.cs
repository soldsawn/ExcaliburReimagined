using Excalibur.Core.Hardware;
using Excalibur.Core.Settings;

namespace Excalibur.Core.Power;

/// <summary>What the service can read and write about power (EC command 0x0300, the adapter, the GPU limit).</summary>
public interface IPowerHardware
{
    /// <summary>Sends the performance mode to the firmware; throws <see cref="PowerException"/> when that fails.</summary>
    void SetMode(PowerMode mode);

    /// <summary>Battery or wall, and whether the adapter is a (weak) USB-C one.</summary>
    PowerSource ReadSource();

    /// <summary>The GPU power limit the driver enforces right now, in watts; null when it cannot be read.</summary>
    int? ReadGpuWatts();

    /// <summary>The CPU's power and limits now; null when they cannot be read.</summary>
    CpuPower? ReadCpuPower() => null;

    /// <summary>The power the GPU draws now, in watts; null when it cannot be read.</summary>
    int? ReadGpuPowerWatts() => null;

    /// <summary>Puts the user's CPU limits in force, or the firmware's when null; throws <see cref="PowerException"/> when that fails.</summary>
    void ApplyCpuLimit(CpuLimit? limit) { }
}

public sealed class PowerException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What a power mode request ended up as; <see cref="Code"/> is stable, for the UI to word.</summary>
public sealed record PowerResult(bool Ok, string? Code = null, string? Message = null)
{
    public static readonly PowerResult Success = new(true);
    public static PowerResult Fail(string code, string message) => new(false, code, message);
}

/// <summary>For the status the apps show.</summary>
public sealed record PowerStatus(
    PowerMode? Mode,
    PowerMode? Preferred,
    PowerSource Source,
    int? GpuWatts,
    PowerLock Lock,
    bool AutomationEnabled,
    CpuPower? Cpu = null,
    int? GpuPowerWatts = null,
    CpuLimit? CpuLimit = null);

/// <summary>
/// The service's power logic (docs/09 A3). It decides, the hardware does:
/// <list type="bullet">
///   <item>the firmware cannot report its mode (GET 0x0300 always returns 0), so the app keeps the
///   user's choice (<see cref="ServiceSettings.PowerMode"/>) and reads the GPU limit to see what is really active;</item>
///   <item>Gaming and High performance only run on the wall adapter: on battery and with a weak
///   USB-C adapter the mode is Office, and the user's choice returns when the charger is back;</item>
///   <item>nothing is written until the user chose a mode or turned automation on, except forcing
///   Office on battery when the GPU limit positively shows a faster mode (unknown = leave it alone);</item>
///   <item>with automation on, plugging the charger in or out applies that side's rule (a mode, a fan profile, or both).</item>
/// </list>
/// Every hardware call is guarded; a failure is logged and reported, never thrown at the caller.
/// Thread-safe: the timer, the pipe and the resume handler all call into it.
/// </summary>
public sealed class PowerManager
{
    private readonly IPowerHardware _hw;
    private readonly ServiceSettingsStore _settings;
    private readonly Action<string, string> _switchProfile;
    private readonly Action<string> _log;
    private readonly object _gate = new();

    private PowerSource? _source;
    private PowerMode? _applied;
    private int? _gpuWatts, _gpuPower;
    // NVML wakes the NVIDIA card, so it is asked only when it matters, as the stock software does (it uses NVML only in
    // its open window): the limit on the polls after start, wake and a mode command, the draw while a window shows it.
    private const int GpuLimitReads = 3;
    private int _gpuLimitReadsLeft = GpuLimitReads;
    private DateTime _detailUntil = DateTime.MinValue;
    private static readonly TimeSpan DetailFor = TimeSpan.FromSeconds(5);
    private CpuPower? _cpu;
    private int _polls;
    private readonly TimeSpan _settle;
    // A change of power source waiting for the EC to settle: (source, first).
    private PowerSource? _pending;
    // How often the CPU limit is checked again (the firmware may reset it, e.g. after sleep): every 30 polls, about a minute.
    private const int CpuLimitCheckEvery = 30;

    /// <param name="switchProfile">(fan profile name, <c>FanSource</c>) — switches the fan profile the way a user command does.</param>
    /// <param name="settle">How long a change of power source waits before the mode and the profile follow: the EC is busy with
    /// the charger then (ACPI 13 at 19:20:58 on 2026-10-23, 5 s after the plug and the mode command). Zero acts at once.</param>
    public PowerManager(IPowerHardware hardware, ServiceSettingsStore settings, Action<string, string> switchProfile, Action<string>? log = null,
        TimeSpan settle = default)
    {
        _settle = settle;
        _hw = hardware;
        _settings = settings;
        _switchProfile = switchProfile;
        _log = log ?? (_ => { });
    }

    public PowerStatus Status
    {
        get
        {
            lock (_gate)
            {
                var source = _source ?? new PowerSource(false, null, false);
                return new PowerStatus(Effective(), _settings.Current.PowerMode, source, _gpuWatts, LockOf(source),
                    _settings.Current.PowerAutomation.Enabled, _cpu, _gpuPower, _settings.Current.CpuLimit);
            }
        }
    }

    /// <summary>
    /// Reads the source and acts when it changed (or on the first call). Call every couple of
    /// seconds; it is cheap when nothing changed.
    /// </summary>
    public void Poll()
    {
        PowerSource source;
        try { source = _hw.ReadSource(); }
        catch (Exception ex) when (ex is PowerException or InvalidOperationException) { _log($"power: could not read the power source ({ex.Message})"); return; }

        lock (_gate)
        {
            if (_gpuLimitReadsLeft > 0)
            {
                _gpuLimitReadsLeft--;
                if (SafeGpuWatts() is { } limit) _gpuWatts = limit;
            }
            _gpuPower = DateTime.UtcNow < _detailUntil ? SafeGpuPower() : null;
            _cpu = SafeCpuPower();
            if (_polls++ % CpuLimitCheckEvery == 0) ApplyCpuLimit(_settings.Current.CpuLimit);
            if (_pending is not null && !EcQuiet.IsQuiet)
            {
                _pending = null;
                // The source may have changed back meanwhile; act on what it is now.
                Apply(source, first: false);
            }
            // The battery percent changes all the time; only a change of power source counts.
            bool sameSource = _source is { } known && known.OnBattery == source.OnBattery && known.UsbCAdapter == source.UsbCAdapter;
            if (sameSource) { _source = source; return; }
            bool first = _source is null;
            _source = source;
            _log($"power: {(first ? "now" : "changed to")} {Describe(source)}");
            if (!first && _settle > TimeSpan.Zero)
            {
                EcQuiet.Hold(_settle, "power source");
                _pending = source;
                return;
            }
            Apply(source, first);
        }
    }

    /// <summary>A window shows the power drawn now: read the GPU's draw for the next few seconds.</summary>
    public void WantDetail()
    {
        lock (_gate) _detailUntil = DateTime.UtcNow + DetailFor;
    }

    /// <summary>After resume from sleep the firmware may have forgotten its mode: apply it again.</summary>
    public void Reapply()
    {
        lock (_gate)
        {
            _gpuLimitReadsLeft = GpuLimitReads;
            ApplyCpuLimit(_settings.Current.CpuLimit);
            if (_source is not { } source) return;
            _applied = null;
            Apply(source, first: true);
        }
    }

    /// <summary>The user picked a mode. Fast modes are refused on battery and with a weak adapter.</summary>
    public PowerResult SetMode(PowerMode mode)
    {
        lock (_gate)
        {
            var source = _source ?? ReadOrDefault();
            if (!PowerModeInfo.IsAllowed(mode, source))
                return LockOf(source) == PowerLock.UsbCAdapter
                    ? PowerResult.Fail("power_usbc", "This USB-C adapter is too weak for that mode; use the wall adapter.")
                    : PowerResult.Fail("power_battery", "That mode needs the charger; on battery only Office is available.");

            var result = Send(mode);
            // The choice is remembered for the wall adapter only; Office on battery is not a preference.
            if (result.Ok && source.CanRunFast) _settings.Update(s => s with { PowerMode = mode }, out _);
            return result;
        }
    }

    /// <summary>The user set their own CPU limits, or null to let the power mode decide again.</summary>
    public PowerResult SetCpuLimit(CpuLimit? limit)
    {
        if (limit?.Validate() is { Count: > 0 } errors) return PowerResult.Fail("cpu_limit_invalid", string.Join("; ", errors));
        lock (_gate)
        {
            if (!ApplyCpuLimit(limit)) return PowerResult.Fail("cpu_limit_failed", "The CPU limit could not be written.");
            _settings.Update(s => s with { CpuLimit = limit }, out _);
            _log(limit is null ? "power: CPU limit back to the power mode" : $"power: CPU limit {limit.Pl1Watts} W / {limit.Pl2Watts} W");
            return PowerResult.Success;
        }
    }

    /// <summary>When the service stops: the firmware's CPU limits go back (the saved choice stays for the next start).</summary>
    public void RestoreCpuLimit()
    {
        lock (_gate) ApplyCpuLimit(null);
    }

    // ---- decisions -------------------------------------------------------------------------

    private bool ApplyCpuLimit(CpuLimit? limit)
    {
        try { _hw.ApplyCpuLimit(limit); return true; }
        catch (Exception ex) when (ex is PowerException or InvalidOperationException)
        {
            _log($"power: could not write the CPU limit ({ex.Message})");
            return false;
        }
    }

    private void Apply(PowerSource source, bool first)
    {
        var settings = _settings.Current;
        var automation = settings.PowerAutomation;
        var rule = automation.Enabled ? automation.RuleFor(source) : null;

        PowerMode? target;
        if (!source.CanRunFast)
            target = PowerMode.Office; // the lock; there is no choice here
        else if (rule?.Mode is { } ruleMode)
            target = ruleMode;
        else
            target = settings.PowerMode; // the user's choice comes back with the charger

        if (target is { } mode)
        {
            // On battery with nothing chosen: only act when a faster mode is really active.
            bool needsForce = !source.CanRunFast && settings.PowerMode is null && !automation.Enabled;
            if (!needsForce || FasterModeActive())
            {
                if (_applied != mode || first) Send(mode);
                if (source.CanRunFast && rule?.Mode is not null) _settings.Update(s => s with { PowerMode = mode }, out _);
            }
        }

        // A fan profile only follows a real change of source, not the service starting or a resume.
        if (rule?.FanProfile is { } profile && !first)
        {
            _log($"power: automation switches the fan profile to {profile}");
            try { _switchProfile(profile, "automation"); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { _log($"power: could not switch the fan profile ({ex.Message})"); }
        }
    }

    private PowerResult Send(PowerMode mode)
    {
        try
        {
            _hw.SetMode(mode);
            _applied = mode;
            _gpuLimitReadsLeft = GpuLimitReads;
            _log($"power: mode {mode} sent");
            return PowerResult.Success;
        }
        catch (Exception ex) when (ex is PowerException or InvalidOperationException)
        {
            _log($"power: could not set mode {mode} ({ex.Message})");
            return PowerResult.Fail("power_failed", $"The firmware did not accept the mode ({ex.Message}).");
        }
    }

    /// <summary>The mode in effect as far as we know: what we last sent, or what the GPU limit says.</summary>
    private PowerMode? Effective()
    {
        if (_gpuWatts is { } w && PowerModeInfo.FromGpuWatts(w) is { } fromGpu) return fromGpu;
        return _applied;
    }

    /// <summary>True only when the GPU limit positively shows Gaming or High performance (70 W is the driver default, not a mode).</summary>
    private bool FasterModeActive() =>
        SafeGpuWatts() is { } w && PowerModeInfo.FromGpuWatts(w) is PowerMode.Gaming or PowerMode.HighPerformance;

    private static PowerLock LockOf(PowerSource s) =>
        s.OnBattery ? PowerLock.OnBattery : s.UsbCAdapter ? PowerLock.UsbCAdapter : PowerLock.None;

    private int? SafeGpuWatts()
    {
        try { return _hw.ReadGpuWatts(); }
        catch (Exception ex) when (ex is PowerException or InvalidOperationException) { return null; }
    }

    private int? SafeGpuPower()
    {
        try { return _hw.ReadGpuPowerWatts(); }
        catch (Exception ex) when (ex is PowerException or InvalidOperationException) { return null; }
    }

    private CpuPower? SafeCpuPower()
    {
        try { return _hw.ReadCpuPower(); }
        catch (Exception ex) when (ex is PowerException or InvalidOperationException) { return null; }
    }

    private PowerSource ReadOrDefault()
    {
        try { return _hw.ReadSource(); }
        catch (Exception ex) when (ex is PowerException or InvalidOperationException) { return new PowerSource(false, null, false); }
    }

    private static string Describe(PowerSource s) =>
        s.OnBattery ? $"battery{(s.BatteryPercent is { } p ? $" {p}%" : "")}" : s.UsbCAdapter ? "wall, weak USB-C adapter" : "wall adapter";
}
