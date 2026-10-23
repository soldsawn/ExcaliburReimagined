using Excalibur.Core.Settings;

namespace Excalibur.Core.Lighting;

/// <summary>
/// The service's keyboard light logic (docs/09 A5). It keeps the user's choice (<see cref="ServiceSettings.Lighting"/>)
/// and sends it again whenever the firmware may have lost it: at start, after sleep, and when the light key changes the
/// level (the new level is stored). Nothing is written while the user never chose a light. The tray reports idle and
/// active (it sees the input; the service runs in session 0); idle turns the light out when the user asked for that.
/// Every firmware call is guarded; a failure is logged, never thrown at the caller. Thread-safe.
/// </summary>
public sealed class LightingManager(KeyboardLight light, ServiceSettingsStore settings, Action<string>? log = null)
{
    private readonly object _gate = new();
    private bool _idle;

    public bool Idle
    {
        get { lock (_gate) return _idle; }
    }

    /// <summary>Stores and shows a new light; returns the validation errors (nothing changes then).</summary>
    public IReadOnlyList<string> Set(KeyboardLighting lighting)
    {
        lock (_gate)
        {
            var errors = settings.Update(s => s with { Lighting = lighting }, out _);
            if (errors.Count > 0) return errors;
            _idle = false;
            Apply();
            return [];
        }
    }

    /// <summary>The tray saw no input for the idle-off time (true) or input again (false).</summary>
    public void SetIdle(bool idle)
    {
        lock (_gate)
        {
            if (settings.Current.Lighting is not { } l || (idle && l.IdleOffMinutes == 0) || idle == _idle) return;
            _idle = idle;
            Apply();
        }
    }

    /// <summary>The light key set a new level: keep it, so the next start and wake show it too.</summary>
    public void OnKey(int brightness)
    {
        lock (_gate)
        {
            if (settings.Current.Lighting is not { } l) return;
            _idle = false;
            if (l.Brightness != brightness) settings.Update(s => s with { Lighting = l with { Brightness = brightness } }, out _);
            Apply();
        }
    }

    /// <summary>Sends the stored light again even when nothing changed (start, after sleep).</summary>
    public void Reapply()
    {
        lock (_gate)
        {
            light.Forget();
            Apply();
        }
    }

    private void Apply()
    {
        if (settings.Current.Lighting is not { } l) return;
        try
        {
            foreach (var (zone, setting) in l.Commands(_idle)) light.Set(zone, setting);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The light is a convenience: it must never take anything else down. Sent again on the next change or wake.
            light.Forget();
            log?.Invoke($"keyboard light: {ex.Message}");
        }
    }
}
