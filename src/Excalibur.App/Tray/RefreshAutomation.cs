using System.Windows.Forms;
using Excalibur.Core.Settings;
using Microsoft.Win32;

namespace Excalibur.App.Tray;

/// <summary>
/// Applies the screen refresh rate rule (docs/08 3.1) in the user's session: when the app starts,
/// when the charger is plugged in or out, after the laptop wakes up, and when the rule changes.
/// Does nothing while the rule is off or a program rule holds the rate (<see cref="Held"/>), and never touches a screen that
/// is not the laptop's own.
/// </summary>
internal sealed class RefreshAutomation : IDisposable
{
    private readonly UserSettingsStore _settings;
    private readonly Action<string, string> _notify;
    private readonly System.Windows.Forms.Timer _delay = new() { Interval = 3000 };
    private bool _lastOnBattery;

    /// <param name="notify">(title, text) — shown to the user when the rate could not be set.</param>
    public RefreshAutomation(UserSettingsStore settings, Action<string, string> notify)
    {
        _settings = settings;
        _notify = notify;
        _lastOnBattery = OnBattery();
        // The driver needs a moment after a change of power source or a wake-up.
        _delay.Tick += (_, _) => { _delay.Stop(); Apply(); };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Schedule();
    }

    /// <summary>True while a program rule set the rate; the charger rule then waits for it to end.</summary>
    public Func<bool>? Held { get; set; }

    /// <summary>Call after the rule was changed in the settings.</summary>
    public void RuleChanged() => Schedule();

    /// <summary>Applies the rule now (a program rule that held the rate ended).</summary>
    public void ApplyNow() => Apply();

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) { Schedule(); return; }
        if (e.Mode != PowerModes.StatusChange) return;
        bool now = OnBattery();
        if (now == _lastOnBattery) return; // the battery percent changed, not the power source
        _lastOnBattery = now;
        Schedule();
    }

    private void Schedule()
    {
        _delay.Stop();
        _delay.Start();
    }

    private void Apply()
    {
        var rule = _settings.Current.RefreshRate;
        if (!rule.Enabled || Held?.Invoke() == true) return;
        var panel = DisplayRefresh.FindInternal();
        if (panel is null) return;

        bool battery = OnBattery();
        int wanted = battery ? rule.OnBatteryHz : rule.OnAcHz == 0 ? panel.SupportedHz.Max() : rule.OnAcHz;
        if (panel.CurrentHz == wanted) return;
        if (!DisplayRefresh.TrySet(panel, wanted))
            _notify("Ekran yenileme hızı değiştirilemedi",
                $"Ekran {wanted} Hz'i desteklemiyor ya da ekran sürücüsü kabul etmedi (desteklenenler: {string.Join(", ", panel.SupportedHz)} Hz).");
    }

    private static bool OnBattery() => SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _delay.Dispose();
    }
}
