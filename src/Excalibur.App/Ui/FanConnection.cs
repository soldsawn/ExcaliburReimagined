using System.Text.Json;
using System.Windows.Threading;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.History;
using Excalibur.Core.Settings;

namespace Excalibur.App.Ui;

/// <summary>
/// The main window's link to the fan service: polls the status every second (events every
/// fifth time) on the UI thread and raises <see cref="Changed"/>. Only exists while the window
/// is open, so the window costs nothing once closed.
/// </summary>
internal sealed class FanConnection : IDisposable
{
    /// <summary>Missed polls before the UI says "not connected" (one miss is not an outage).</summary>
    private const int OfflineAfter = 2;

    private readonly FanPipeClient _client;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _failures, _ticks;
    private bool _busy;

    public FanConnection(bool simulated)
    {
        _client = new FanPipeClient(simulated ? FanPipe.SimName : FanPipe.Name);
        _timer.Tick += async (_, _) =>
        {
            await PollAsync(withEvents: ++_ticks % 5 == 0);
            if (_ticks % 10 == 0) await RefreshProfilesAsync();
        };
    }

    public event Action? Changed;

    public FanStatusDto? Status { get; private set; }
    public IReadOnlyList<FanEventDto> Events { get; private set; } = [];
    public bool Connected => _failures < OfflineAfter && Status is not null;
    public DateTime? LastUpdate { get; private set; }

    /// <summary>The profiles and quick-access slots; refreshed on start, every tenth poll and after a profile command.</summary>
    public ProfileCatalog Catalog { get; } = new();

    /// <summary>The service's settings as last read (null until the first <see cref="RefreshSettingsAsync"/>).</summary>
    public ServiceSettings? Settings { get; private set; }

    public void Start()
    {
        _timer.Start();
        _ = PollAsync(withEvents: true);
        _ = RefreshProfilesAsync();
    }

    /// <summary>Sends a command and returns the whole reply (error code included); null when the service cannot be reached.</summary>
    public async Task<FanResponse?> RequestAsync(FanRequest request, TimeSpan? replyTimeout = null)
    {
        try
        {
            var r = await _client.SendAsync(request with { Source = FanSource.Window }, replyTimeout: replyTimeout);
            if (r.Status is { } s) Apply(s, null);
            if (r.Profiles is not null) Catalog.Update(r);
            if (r.Settings is { } settings) Settings = settings;
            return r;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task RefreshProfilesAsync() => await RequestAsync(new FanRequest("listProfiles"));

    public async Task RefreshSettingsAsync()
    {
        if (await RequestAsync(new FanRequest("getSettings")) is { Ok: true, Settings: { } s }) Settings = s;
    }

    /// <summary>Sends a command; returns the service's error text, or null when it was accepted.</summary>
    public async Task<string?> SendAsync(FanRequest request)
    {
        try
        {
            var r = await _client.SendAsync(request with { Source = FanSource.Window });
            if (r.Status is { } s) Apply(s, null);
            _ = PollAsync(withEvents: true);
            return r.Ok ? null : r.Error ?? "reddedildi";
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or JsonException)
        {
            return $"Fan servisine ulaşılamadı: {ex.Message}";
        }
    }

    /// <summary>The service's history for the last <paramref name="span"/>; null when unreachable or recording is off.</summary>
    public async Task<HistorySeries?> HistoryAsync(TimeSpan span, int maxPoints)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var r = await _client.SendAsync(new FanRequest("history", Source: FanSource.Window, From: now - span, To: now, MaxPoints: maxPoints));
            return r.Ok ? r.History : null;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task PollAsync(bool withEvents)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var r = await _client.SendAsync(new FanRequest(withEvents ? "events" : "status", Source: FanSource.Window));
            if (r.Status is { } s) Apply(s, r.Events);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or JsonException)
        {
            _failures++;
            Changed?.Invoke();
        }
        finally
        {
            _busy = false;
        }
    }

    private void Apply(FanStatusDto s, IReadOnlyList<FanEventDto>? events)
    {
        _failures = 0;
        Status = s;
        if (events is not null) Events = events;
        LastUpdate = DateTime.Now;
        Changed?.Invoke();
    }

    public void Dispose() => _timer.Stop();
}
