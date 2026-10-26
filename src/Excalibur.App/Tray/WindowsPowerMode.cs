using System.Runtime.InteropServices;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.App.Tray;

/// <summary>
/// Makes Windows' own power mode (Settings > Power: best power efficiency / balanced / best performance) follow the
/// laptop's performance mode, as the stock software does with its power plans. Off by default (<see cref="UserSettings.WindowsPowerMode"/>):
/// many users set the Windows side themselves. Windows keeps one mode for the charger and one for the battery, so the
/// pair is applied again when the power source changes. Windows ignores it while a plan other than Balanced is active.
/// </summary>
internal sealed class WindowsPowerMode(UserSettingsStore settings)
{
    private static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid Balanced = Guid.Empty;
    private static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private (PowerMode Mode, bool OnBattery)? _applied;

    /// <summary>Call with every status; writes only when the option is on and the mode or the power source changed.</summary>
    public void Follow(PowerStatusDto? power)
    {
        if (!settings.Current.WindowsPowerMode) { _applied = null; return; }
        if (power?.Mode is not { } mode) return;
        var key = (mode, power.OnBattery);
        if (_applied == key) return;
        var overlay = mode switch
        {
            PowerMode.Office => BestEfficiency,
            PowerMode.Gaming => Balanced,
            _ => BestPerformance,
        };
        uint result = PowerSetActiveOverlayScheme(overlay);
        TrayLog.Write($"windows power mode: {mode} -> {overlay} ({(result == 0 ? "ok" : $"error {result}")})");
        // A refusal is not retried every poll; the next mode or source change tries again.
        _applied = key;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);
}
