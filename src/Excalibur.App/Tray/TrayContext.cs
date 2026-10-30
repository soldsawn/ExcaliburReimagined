using System.Drawing;
using System.Windows.Forms;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.App.Tray;

/// <summary>
/// Owns the tray icon and polls the service: every 2 s, or every second while the panel is
/// open. The icon shows the CPU temperature on a tile colored by the hotter chip; a profile
/// change (from here, the test window or the service) flashes the OSD; a fault or a lost
/// service raises a Windows notification once.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    public const string AppName = "Excalibur Reimagined";
    /// <summary>The menu item and panel link that open the main window.</summary>
    public const string OpenLabel = "Excalibur Reimagined'ı aç";
    private const int IdleMs = 2000, OpenMs = 1000;
    /// <summary>Missed polls before the icon goes grey (one miss is not an outage).</summary>
    private const int OfflineAfter = 2;
    /// <summary>Missed polls before the "service unreachable" notification (~10 s).</summary>
    private const int NotifyOfflineAfter = 5;

    private readonly FanPipeClient _client;
    private readonly ProfileCatalog _catalog = new();
    private int _polls;
    private readonly bool _simulated;
    private readonly NotifyIcon _tray = new() { Text = AppName };
    private readonly TrayIconRenderer _icons = new();
    private readonly ContextMenuStrip _menu = new() { Font = new Font("Segoe UI", 9f) };
    private static readonly (PowerMode Mode, string Label)[] PowerModes =
    [
        (PowerMode.Office, "Ofis modu"),
        (PowerMode.Gaming, "Oyun modu"),
        (PowerMode.HighPerformance, "Yüksek performans"),
    ];
    private readonly List<ToolStripMenuItem> _powerItems = [];
    private readonly ToolStripMenuItem _stateItem = new() { Tag = "state" };
    private readonly List<ToolStripMenuItem> _profileItems = [];
    private readonly TrayPanel _panel = new();
    private readonly OsdForm _osd = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = IdleMs };
    private readonly UserSettingsStore _userSettings = new();
    private readonly RefreshAutomation _refresh;
    private readonly AppAutomation? _apps;
    private readonly WindowsPowerMode? _windowsPower;

    private FanStatusDto? _status;
    private int _failures;
    private bool _busy, _offlineNotified, _rebuildPending;
    private string? _lastProfile;
    private FanLoopMode? _lastMode;

    public TrayContext(bool simulated, string? snapshot)
    {
        _simulated = simulated;
        _client = new FanPipeClient(simulated ? FanPipe.SimName : FanPipe.Name);
        BuildMenu();
        _tray.ContextMenuStrip = _menu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };
        _tray.BalloonTipClicked += (_, _) => _panel.Popup();

        _panel.ProfileClicked += name => _ = SendAsync(new FanRequest("setProfile", Profile: name));
        _panel.ClearFaultClicked += () => _ = SendAsync(new FanRequest("clearFault"));
        // The main window saves appearance settings; follow them (the event comes on a pool thread).
        var ui = SynchronizationContext.Current;
        Theme.Settings = _userSettings.Current;
        _userSettings.Changed += s => ui?.Post(_ =>
        {
            Theme.Settings = s; // the panel, menu and notice read it when next shown
            Render();
            _refresh?.RuleChanged();
        }, null);
        _refresh = new RefreshAutomation(_userSettings, (title, text) => Notify(title, text, ToolTipIcon.Warning));
        // Not while taking snapshots: those only read.
        if (snapshot is null)
        {
            _apps = new AppAutomation(_client, _userSettings, _refresh,
                (title, subtitle) => { if (_userSettings.Current.Osd) _osd.Flash(title, subtitle, Theme.Ok); });
            _refresh.Held = () => _apps.HoldsRefreshRate;
            _windowsPower = new WindowsPowerMode(_userSettings);
        }
        _panel.DetailsClicked += OpenMainWindow;
        _panel.VisibleChanged += (_, _) => { _timer.Interval = _panel.Visible ? OpenMs : IdleMs; if (_panel.Visible) _ = PollAsync(); };

        Render();
        _tray.Visible = true;
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();

        if (snapshot is not null) SnapshotAfterFirstPoll(snapshot);
    }

    private void BuildMenu()
    {
        _stateItem.Font = new Font(_menu.Font, FontStyle.Bold);
        _menu.Items.Add(_stateItem);
        _menu.Items.Add(new ToolStripSeparator());
        RebuildProfileItems();
        // Power mode (GPU power limit): a separate choice from the fan profile above.
        _menu.Items.Add(new ToolStripSeparator());
        foreach (var (mode, label) in PowerModes)
        {
            var item = new ToolStripMenuItem(label) { Tag = mode };
            item.Click += (_, _) => _ = SendAsync(new FanRequest("setPowerMode", PowerMode: mode));
            _powerItems.Add(item);
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new ToolStripSeparator());
        // The main window is the app; the quick panel is what a left click on the icon opens.
        var open = new ToolStripMenuItem(OpenLabel, null, (_, _) => OpenMainWindow());
        open.Font = new Font(_menu.Font, FontStyle.Bold);
        _menu.Items.Add(open);
        _menu.Items.Add("Hızlı panel", null, (_, _) => _panel.Popup());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Çıkış", null, (_, _) => { TrayLog.Write("exit: clicked"); _ = ExitAsync(); })
        {
            ToolTipText = "Simgeyi, açık pencereyi ve arka plandaki fan servisini kapatır; fanları bilgisayarın kendi düzenine bırakır.",
        });
        _menu.Items.Add(new ToolStripSeparator()); // empty tail: keeps the last card off the menu's bottom edge
        _menu.ShowItemToolTips = true;
        foreach (ToolStripItem item in _menu.Items)
        {
            if (item is ToolStripMenuItem) item.Padding = new Padding(20, 6, 18, 6);
            else if (item is ToolStripSeparator) { item.AutoSize = false; item.Height = 2 * ThemedMenuRenderer.CardPadding + 6; }
        }
        _menu.Padding = new Padding(0, ThemedMenuRenderer.CardPadding + 1, 0, ThemedMenuRenderer.CardPadding + 8);
        _menu.Opening += (_, _) => { TrayLog.Write("menu: opening"); ApplyMenuTheme(); RenderMenu(); _ = RefreshProfilesAsync(); };
        _menu.ItemClicked += (_, e) => TrayLog.Write($"menu: item clicked '{e.ClickedItem?.Text}'");
        _menu.Closed += (_, e) =>
        {
            TrayLog.Write($"menu: closed ({e.CloseReason})");
            if (_rebuildPending) { _rebuildPending = false; RebuildProfileItems(); }
        };
        // Rebuilding while the menu is open moves the items under the cursor, so the click misses; wait for it to close.
        _catalog.Changed += () => { if (_menu.Visible) _rebuildPending = true; else RebuildProfileItems(); };
    }

    /// <summary>One menu entry per quick-access slot (built-in or custom), right after the status line and its gap.</summary>
    private void RebuildProfileItems()
    {
        foreach (var old in _profileItems) _menu.Items.Remove(old);
        _profileItems.Clear();
        int at = _menu.Items.Contains(_stateItem) ? _menu.Items.IndexOf(_stateItem) + 2 : _menu.Items.Count;
        foreach (var (name, _) in _catalog.SlotButtons())
        {
            var item = new ToolStripMenuItem(ProfileNames.Label(name)) { Tag = name };
            item.Padding = new Padding(20, 6, 18, 6);
            item.Click += (_, _) => _ = SendAsync(new FanRequest("setProfile", Profile: name));
            _profileItems.Add(item);
            _menu.Items.Insert(at++, item);
        }
        _panel.SetSlots(_catalog.SlotButtons());
    }

    /// <summary>Asks the service for the profile list and the slots; failures are ignored (the next poll retries).</summary>
    private async Task RefreshProfilesAsync()
    {
        try
        {
            var r = await _client.SendAsync(new FanRequest("listProfiles"));
            if (r.Ok) _catalog.Update(r);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or System.Text.Json.JsonException) { }
    }

    /// <summary>Re-reads the Windows theme on every open, so a switch to light/dark is picked up.</summary>
    private void ApplyMenuTheme()
    {
        var theme = Theme.Current;
        _menu.Renderer = new ThemedMenuRenderer(theme);
        _menu.BackColor = theme.Back;
        _menu.ForeColor = theme.Text;
        Native.Round(_menu.Handle, theme.IsDark);
    }

    private void TogglePanel()
    {
        if (_panel.Visible) { _panel.Hide(); return; }
        // A click on the icon first deactivates (and so hides) an open panel; do not reopen it.
        if ((DateTime.UtcNow - _panel.HiddenAt).TotalMilliseconds < 300) return;
        _panel.Popup();
    }

    private bool Connected => _failures < OfflineAfter && _status is not null;

    private async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var r = await _client.SendAsync(new FanRequest("status"));
            if (r.Status is { } s)
            {
                Apply(s);
                await LightIdle.SyncAsync(s, _client);
            }
            // The profile list changes rarely (the window edits it); every tenth poll, and when the menu opens, is plenty.
            if (_polls++ % 10 == 0) await RefreshProfilesAsync();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            _failures++;
            if (_failures >= NotifyOfflineAfter && !_offlineNotified)
            {
                _offlineNotified = true;
                Notify("Fan kontrolüne ulaşılamıyor",
                    "Fan kontrol hizmeti yanıt vermiyor. Gerekirse fanlar orijinal ayara bırakılır; hizmet birkaç saniye içinde kendini yeniden başlatır.",
                    ToolTipIcon.Warning);
            }
            Render();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Asks the service (and through it the guardian) to stop, then closes the icon. A service that does not answer must not keep the icon open.</summary>
    private async Task ExitAsync()
    {
        try
        {
            var send = _client.SendAsync(new FanRequest("shutdown", Source: FanSource.Tray));
            var done = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(3)));
            TrayLog.Write(done == send ? $"exit: shutdown sent ({send.Status})" : "exit: no answer in 3 s");
        }
        catch (Exception ex) { TrayLog.Write($"exit: shutdown failed ({ex.GetType().Name})"); }
        // Whatever goes wrong closing the window, the icon must still go (a lost exception here used to keep it open).
        try { AppInstances.CloseMainWindow(); }
        catch (Exception ex) { TrayLog.Write($"exit: closing the window failed ({ex.GetType().Name}: {ex.Message})"); throw; }
        finally
        {
            TrayLog.Write("exit: ExitThread");
            ExitThread();
        }
    }

    private async Task SendAsync(FanRequest request)
    {
        try
        {
            var r = await _client.SendAsync(request with { Source = FanSource.Tray });
            if (!r.Ok)
            {
                _panel.CommandFailed();
                Notify("İstek kabul edilmedi", r.Code switch
                {
                    "power_battery" => "Pilde yalnızca Ofis modu kullanılabilir.",
                    "power_usbc" => "Bu USB-C adaptörde yalnızca Ofis modu kullanılabilir.",
                    "power_failed" => "Güç modu değiştirilemedi.",
                    _ => r.Error ?? request.Cmd,
                }, ToolTipIcon.Warning);
            }
            // Our own change: show the notice now instead of on the next poll.
            if (r.Ok && r.Status is { } changed && changed.Profile != _lastProfile) Flash(changed);
            if (r.Status is { } s) Apply(s);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            _panel.CommandFailed();
            Notify("Fan kontrolüne ulaşılamadı", "Birkaç saniye sonra yeniden deneyin.", ToolTipIcon.Warning);
        }
    }

    private void Apply(FanStatusDto s)
    {
        bool wasOffline = _offlineNotified;
        _failures = 0;
        _offlineNotified = false;
        _status = s;

        // Changes made here were flashed when sent (a program rule's with its own notice); the main window's own changes need
        // no notice (it is on screen); anything else (the service, automation) flashes when a poll sees it.
        if (_lastProfile is not null && s.Profile != _lastProfile && s.ProfileChangedBy is not (FanSource.Tray or FanSource.Window or FanSource.App))
            Flash(s);
        _apps?.Resync(s);
        _windowsPower?.Follow(s.Power);
        if (s.Mode == FanLoopMode.Faulted && _lastMode is not null && _lastMode != FanLoopMode.Faulted)
            Notify("Fanlarda bir sorun oldu",
                "Fanlar güvenlik için orijinal ayara bırakıldı. Hızlı paneldeki \"Tekrar dene\" ile yeniden başlatabilirsiniz.",
                ToolTipIcon.Error);
        if (wasOffline)
            Notify("Fan kontrolü yeniden bağlandı", $"Profil: {ProfileNames.Label(s.Profile, s.ManualUntil)}", ToolTipIcon.Info);

        _lastProfile = s.Profile;
        _lastMode = s.Mode;
        Render();
    }

    private void Flash(FanStatusDto s)
    {
        if (_userSettings.Current.Osd)
            _osd.Flash(ProfileNames.Label(s.Profile, s.ManualUntil), "Fan profili", Theme.Ok);
    }

    private void Render()
    {
        var s = _status;
        bool connected = Connected;
        _panel.ShowStatus(s, connected);

        if (!connected || s is null)
        {
            _tray.Icon = _icons.Render("-", Theme.Off, _userSettings.Current.TrayFont);
            _tray.Text = $"{AppName}: fan kontrolüne bağlanılamıyor";
            return;
        }

        int? hottest = Max(s.CpuTempC, s.GpuTempC);
        // Like the Windows volume/network icons: plain glyphs in the taskbar's color; only a warm or
        // hot reading (or a fault) gets a color.
        var (text, color) = s.Mode switch
        {
            FanLoopMode.Faulted => ("!", Theme.Bad),
            _ => (s.CpuTempC?.ToString() ?? "-",
                  s.Mode == FanLoopMode.MonitorOnly ? Theme.Warn : Theme.Temperature(hottest, Theme.TaskbarText)),
        };
        _tray.Icon = _icons.Render(text, color, _userSettings.Current.TrayFont);

        // NotifyIcon.Text is limited to 127 characters.
        string tip = $"{AppName} · {ProfileNames.Label(s.Profile, s.ManualUntil)}\n"
            + $"İşlemci {s.CpuTempC} °C · {s.CpuRpm} rpm\nEkran kartı {(s.GpuTempC is { } g ? $"{g} °C" : "kapalı")} · {s.GpuRpm} rpm"
            + (s.Mode == FanLoopMode.Faulted ? "\nSorun var: fanlar orijinal ayarda" : "");
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    private void RenderMenu()
    {
        var s = _status;
        _stateItem.Text = !Connected || s is null ? "Fan kontrolüne bağlanılamıyor"
            : $"CPU {s.CpuTempC}°  ·  GPU {(s.GpuTempC is { } g ? $"{g}°" : "kapalı")}  ·  {ProfileNames.Label(s.Profile, s.ManualUntil)}";
        foreach (var item in _profileItems)
        {
            item.Checked = Connected && s?.Profile == (string)item.Tag!;
            item.Enabled = Connected;
        }
        foreach (var item in _powerItems)
        {
            var mode = (PowerMode)item.Tag!;
            item.Checked = Connected && s?.Power?.Mode == mode;
            // On battery or a weak USB-C adapter only Office is allowed (the laptop forces it).
            item.Enabled = Connected && (mode == PowerMode.Office || s?.Power is not { Lock: not PowerLock.None });
            // The graphics card's limit in that mode on this laptop, once the service has measured it.
            string label = PowerModes.First(x => x.Mode == mode).Label;
            item.Text = s?.Power?.Modes?.FirstOrDefault(x => x.Mode == mode) is { GpuWatts: { } gpu } ? $"{label} · {gpu} W" : label;
        }
    }

    private void Notify(string title, string text, ToolTipIcon icon) => _tray.ShowBalloonTip(5000, title, text, icon);

    private static int? Max(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private void OpenMainWindow()
    {
        try
        {
            AppInstances.OpenMainWindow(_simulated);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Notify("Ana pencere açılamadı", ex.Message, ToolTipIcon.Error);
        }
    }

    /// <summary>Development: open the panel, flash the OSD, save both to PNG files and exit.</summary>
    private void SnapshotAfterFirstPoll(string path)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 3500 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _panel.Popup();
            _osd.Flash("Dengeli", "Fan profili", Theme.Ok);
            Application.DoEvents();
            using (var bmp = new Bitmap(_panel.Width, _panel.Height))
            {
                _panel.DrawToBitmap(bmp, new Rectangle(Point.Empty, _panel.Size));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            using (var osd = _osd.Snapshot())
                osd.Save(Path.ChangeExtension(path, ".osd.png"), System.Drawing.Imaging.ImageFormat.Png);
            ApplyMenuTheme();
            RenderMenu();
            _menu.Show(new Point(200, 200));
            _menu.Items[3].Select(); // show the hover highlight in the picture
            Application.DoEvents();
            using (var menu = new Bitmap(_menu.Width, _menu.Height))
            {
                _menu.DrawToBitmap(menu, new Rectangle(Point.Empty, _menu.Size));
                menu.Save(Path.ChangeExtension(path, ".menu.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            _menu.Close();
            // The icon 8x, pixel for pixel, on a dark and a light taskbar background.
            using (var icon = _tray.Icon!.ToBitmap())
            using (var big = new Bitmap(icon.Width * 8 * 2, icon.Height * 8))
            using (var g = Graphics.FromImage(big))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.Clear(Color.FromArgb(32, 32, 32));
                g.DrawImage(icon, 0, 0, icon.Width * 8, icon.Height * 8);
                big.Save(Path.ChangeExtension(path, ".icon.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            ExitThread();
        };
        timer.Start();
    }

    protected override void ExitThreadCore()
    {
        TrayLog.Write("exit: ExitThreadCore");
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _icons.Dispose();
        _panel.Dispose();
        _osd.Dispose();
        _menu.Dispose();
        _apps?.Dispose();
        _refresh.Dispose();
        _userSettings.Dispose();
        base.ExitThreadCore();
        TrayLog.Write("exit: ExitThreadCore done");
    }
}
