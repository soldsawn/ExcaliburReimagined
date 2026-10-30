using System.Diagnostics;
using Excalibur.Core.Automation;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.App.Tray;

/// <summary>
/// The program rules (A6) in the user's session: every 3 s looks at the running programs (no EC, no service call), and when
/// the rule in force changes sets the screen refresh rate itself and sends the rest to the service (<c>setAppOverride</c>),
/// which layers it over the saved choices. A refresh rate the user changed meanwhile is left as it is when the program closes.
/// </summary>
internal sealed class AppAutomation : IDisposable
{
    private readonly FanPipeClient _client;
    private readonly UserSettingsStore _settings;
    private readonly RefreshAutomation _refresh;
    private readonly Action<string, string> _notice;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private readonly AppRuleTracker _tracker = new();
    // The rate before the first rule that set one, and the rate we set (null = no rule holds the rate).
    private int? _hzBefore, _hzSet;
    private bool _busy;

    /// <param name="notice">(title, subtitle) — the on-screen notice when a rule starts or ends.</param>
    public AppAutomation(FanPipeClient client, UserSettingsStore settings, RefreshAutomation refresh, Action<string, string> notice)
    {
        _client = client;
        _settings = settings;
        _refresh = refresh;
        _notice = notice;
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
    }

    /// <summary>A rule set the refresh rate: the charger rule waits until it ends.</summary>
    public bool HoldsRefreshRate => _hzSet is not null;

    /// <summary>
    /// Called with each status: a service that restarted (or another tray that left a rule behind) does not hold the rule in
    /// force, so it is sent again, without a notice.
    /// </summary>
    public void Resync(FanStatusDto s)
    {
        if (_busy || s.ProtocolVersion < 11) return;
        if (!string.Equals(_tracker.Active?.Program, s.AppProgram, StringComparison.OrdinalIgnoreCase))
            _ = SendAsync(_tracker.Active);
    }

    private async Task CheckAsync()
    {
        if (_busy) return;
        var before = _tracker.Active;
        // Without rules nothing can be in force, so the programs are not listed (a rule just deleted still ends here).
        var rules = _settings.Current.AppRules;
        if (!_tracker.Update(rules, rules.Count == 0 ? NoPrograms : RunningPrograms())) return;
        var rule = _tracker.Active;
        bool rateChanged = ApplyRefresh(rule);
        var status = await SendAsync(rule);
        // An edited rule of the same program needs no notice.
        if (string.Equals(before?.Program, rule?.Program, StringComparison.OrdinalIgnoreCase)) return;
        var parts = new List<string>();
        if (status is not null) parts.Add(ProfileNames.Label(status.Profile, status.ManualUntil));
        if (rule?.PowerMode is { } mode && status?.Power is { Lock: PowerLock.None }) parts.Add(ModeLabel(mode));
        if ((rule?.RefreshHz is not null || rateChanged) && DisplayRefresh.FindInternal() is { } panel) parts.Add($"{panel.CurrentHz} Hz");
        _notice(parts.Count > 0 ? string.Join(" · ", parts) : "Önceki ayarlar",
            rule is not null ? $"{rule.Program} açık" : $"{before!.Program} kapandı");
    }

    /// <summary>Sets the rule's rate, or puts the earlier one back; true when the rate went back.</summary>
    private bool ApplyRefresh(AppRule? rule)
    {
        var panel = DisplayRefresh.FindInternal();
        if (panel is null) return false;
        if (rule?.RefreshHz is { } hz)
        {
            if (!panel.SupportedHz.Contains(hz)) return false;
            _hzBefore ??= panel.CurrentHz;
            if (panel.CurrentHz == hz || DisplayRefresh.TrySet(panel, hz)) _hzSet = hz;
            return false;
        }
        if (_hzSet is not { } set) return false;
        int? back = _hzBefore;
        _hzSet = _hzBefore = null;
        if (panel.CurrentHz != set) return false; // the user chose another rate meanwhile; it stays
        if (_settings.Current.RefreshRate.Enabled) _refresh.ApplyNow();
        else if (back is { } b && b != set) DisplayRefresh.TrySet(panel, b);
        return true;
    }

    private async Task<FanStatusDto?> SendAsync(AppRule? rule)
    {
        _busy = true;
        try
        {
            var r = await _client.SendAsync(new FanRequest("setAppOverride", Source: FanSource.App, AppOverride: rule?.ToOverride()));
            return r.Status;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return null; // the next status shows the service without it, and Resync sends it again
        }
        finally
        {
            _busy = false;
        }
    }

    private static readonly HashSet<string> NoPrograms = [];

    private static HashSet<string> RunningPrograms()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
            using (p) names.Add(p.ProcessName + ".exe");
        return names;
    }

    private static string ModeLabel(PowerMode mode) => mode switch
    {
        PowerMode.Office => "Ofis modu",
        PowerMode.Gaming => "Oyun modu",
        _ => "Yüksek performans",
    };

    public void Dispose() => _timer.Dispose();
}
