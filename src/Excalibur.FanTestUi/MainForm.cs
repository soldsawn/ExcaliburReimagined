using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;

namespace Excalibur.FanTestUi;

/// <summary>
/// One window: live readings, profile picker, time-limited manual mode, "hand to EC",
/// the curve, and recent events. Polls the service once a second over the pipe.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly (string Name, string Label)[] Profiles =
    [
        ("EcAuto", "EC otomatik (stok)"),
        ("Quiet", "Sessiz"),
        ("Balanced", "Dengeli"),
        ("Performance", "Performans"),
    ];

    private readonly RadioButton _real = new() { Text = "Gerçek servis", AutoSize = true };
    private readonly RadioButton _sim = new() { Text = "Simülasyon", AutoSize = true };
    private readonly Label _conn = new() { AutoSize = true, Padding = new Padding(12, 4, 0, 0) };
    private readonly Label _mode = new() { AutoSize = true, Padding = new Padding(12, 4, 0, 0), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };

    private readonly Label[,] _cells = new Label[2, 4]; // [cpu/gpu, temp/rpm/target/applied]
    private readonly Label _boost = new() { AutoSize = true };
    private readonly Label _info = new() { AutoSize = true };

    private readonly ComboBox _profile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly Button _applyProfile = new() { Text = "Uygula", AutoSize = true };

    private readonly TrackBar _cpuManual = new() { Minimum = SafetyPolicy.AbsoluteFloor, Maximum = 100, Value = 60, TickFrequency = 5, Width = 190 };
    private readonly TrackBar _gpuManual = new() { Minimum = SafetyPolicy.AbsoluteFloor, Maximum = 100, Value = 60, TickFrequency = 5, Width = 190 };
    private readonly Label _cpuManualValue = new() { AutoSize = true };
    private readonly Label _gpuManualValue = new() { AutoSize = true };
    private readonly ComboBox _minutes = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    private readonly Button _applyManual = new() { Text = "Manueli uygula", AutoSize = true };

    private readonly Button _release = new() { Text = "EC'ye bırak (0xFF)", AutoSize = true, BackColor = Color.Firebrick, ForeColor = Color.White };
    private readonly Button _clearFault = new() { Text = "Arızayı temizle", AutoSize = true, Enabled = false };
    private readonly Label _message = new() { AutoSize = true, MaximumSize = new Size(320, 0) };

    private readonly CurveView _curve = new() { Dock = DockStyle.Fill };
    private readonly ListBox _events = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private FanPipeClient _client;
    private bool _busy, _profileDirty;
    private int _ticks;

    public MainForm(bool startSimulated)
    {
        Text = "Excalibur Reimagined — Gelişmiş fan kontrolü";
        ClientSize = new Size(980, 640);
        MinimumSize = new Size(820, 560);
        _client = new FanPipeClient(startSimulated ? FanPipe.SimName : FanPipe.Name);
        (startSimulated ? _sim : _real).Checked = true;

        BuildLayout();
        WireEvents();
        _timer.Start();
        _ = RefreshAsync(withEvents: true);
    }

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        top.Controls.AddRange([_real, _sim, _conn, _mode]);

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, Width = 362, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(8, 0, 0, 8),
        };
        left.Controls.Add(Readings());
        left.Controls.Add(Group("Profil", Row(_profile, _applyProfile)));

        foreach (var m in new[] { 1, 5, 10 }) _minutes.Items.Add($"{m} dk");
        _minutes.SelectedIndex = 0;
        left.Controls.Add(Group("Süreli manuel (en çok 10 dk, güvenlik tabanları yine geçerli)",
            Row(new Label { Text = "CPU", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _cpuManual, _cpuManualValue),
            Row(new Label { Text = "GPU", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _gpuManual, _gpuManualValue),
            Row(new Label { Text = "Süre", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _minutes, _applyManual)));

        left.Controls.Add(Row(_release, _clearFault));
        left.Controls.Add(_message);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(4, 0, 8, 8) };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        right.Controls.Add(_curve, 0, 0);
        right.Controls.Add(_events, 0, 1);

        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(top);

        foreach (var (_, label) in Profiles) _profile.Items.Add(label);
        UpdateManualLabels();
    }

    private GroupBox Readings()
    {
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 5, RowCount = 3 };
        string[] headers = ["", "Sıcaklık", "RPM", "Hedef", "Uygulanan"];
        for (int c = 0; c < headers.Length; c++)
            grid.Controls.Add(new Label { Text = headers[c], AutoSize = true, ForeColor = Color.Gray }, c, 0);
        string[] rows = ["CPU", "GPU"];
        var big = new Font(SystemFonts.DefaultFont.FontFamily, 12, FontStyle.Bold);
        for (int r = 0; r < 2; r++)
        {
            grid.Controls.Add(new Label { Text = rows[r], AutoSize = true, Font = big }, 0, r + 1);
            for (int c = 0; c < 4; c++)
            {
                _cells[r, c] = new Label { Text = "–", AutoSize = true, Font = big, Padding = new Padding(4, 0, 8, 0) };
                grid.Controls.Add(_cells[r, c], c + 1, r + 1);
            }
        }
        return Group("Ölçümler", grid, _boost, _info);
    }

    private static GroupBox Group(string title, params Control[] content)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        flow.Controls.AddRange(content);
        var box = new GroupBox { Text = title, AutoSize = true, Width = 330, Padding = new Padding(6) };
        box.Controls.Add(flow);
        return box;
    }

    private static FlowLayoutPanel Row(params Control[] content)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.AddRange(content);
        return row;
    }

    private void WireEvents()
    {
        _timer.Tick += async (_, _) => await RefreshAsync(withEvents: ++_ticks % 5 == 0);
        _real.CheckedChanged += (_, _) => SwitchPipe();
        _profile.SelectionChangeCommitted += (_, _) => _profileDirty = true;
        _cpuManual.ValueChanged += (_, _) => UpdateManualLabels();
        _gpuManual.ValueChanged += (_, _) => UpdateManualLabels();

        _applyProfile.Click += async (_, _) =>
        {
            if (_profile.SelectedIndex < 0) return;
            _profileDirty = false;
            await CommandAsync(new FanRequest("setProfile", Profile: Profiles[_profile.SelectedIndex].Name), "Profil uygulandı.");
        };
        _applyManual.Click += async (_, _) =>
        {
            int minutes = new[] { 1, 5, 10 }[_minutes.SelectedIndex];
            await CommandAsync(new FanRequest("setManual", Cpu: _cpuManual.Value, Gpu: _gpuManual.Value, Minutes: minutes),
                $"Manuel: CPU %{_cpuManual.Value}, GPU %{_gpuManual.Value}, {minutes} dk.");
        };
        _release.Click += async (_, _) => await CommandAsync(new FanRequest("release"), "Fanlar EC'ye bırakıldı (profil: EC otomatik).");
        _clearFault.Click += async (_, _) => await CommandAsync(new FanRequest("clearFault"), "Arıza temizlendi.");
    }

    private void SwitchPipe()
    {
        _client = new FanPipeClient(_real.Checked ? FanPipe.Name : FanPipe.SimName);
        _events.Items.Clear();
        _profileDirty = false;
        _ = RefreshAsync(withEvents: true);
    }

    private void UpdateManualLabels()
    {
        _cpuManualValue.Text = $"%{_cpuManual.Value} (~{_cpuManual.Value * 54} rpm)";
        _gpuManualValue.Text = $"%{_gpuManual.Value} (~{_gpuManual.Value * 54} rpm)";
    }

    private async Task CommandAsync(FanRequest request, string okText)
    {
        try
        {
            var r = await _client.SendAsync(request);
            ShowMessage(r.Ok ? okText : $"Reddedildi: {r.Error}", r.Ok);
            if (r.Status is { } s) Render(s);
            await RefreshAsync(withEvents: true);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            ShowMessage($"Servise ulaşılamadı: {ex.Message}", ok: false);
        }
    }

    private int _failedRefreshes;

    private async Task RefreshAsync(bool withEvents)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var r = await _client.SendAsync(new FanRequest(withEvents ? "events" : "status"));
            _failedRefreshes = 0;
            _conn.Text = $"bağlı: {_client.PipeName}";
            _conn.ForeColor = Color.SeaGreen;
            if (r.Status is { } s) Render(s);
            if (r.Events is { } events) RenderEvents(events);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            // One missed poll is not an outage; only a second one in a row turns the label red.
            if (++_failedRefreshes >= 2)
            {
                _conn.Text = $"bağlanamadı: {_client.PipeName} (servis çalışıyor mu?)";
                _conn.ForeColor = Color.Firebrick;
                _mode.Text = "";
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void Render(FanStatusDto s)
    {
        _mode.Text = s.Mode switch
        {
            FanLoopMode.Running => "Çalışıyor",
            FanLoopMode.MonitorOnly => "Yalnız izleme (EC sürümü doğrulanmamış)",
            FanLoopMode.Faulted => "ARIZA: fanlar EC'de",
            _ => "Durdu",
        } + (s.Simulated ? "  [SİMÜLASYON]" : "");
        _mode.ForeColor = s.Mode == FanLoopMode.Faulted ? Color.Firebrick : s.Mode == FanLoopMode.Running ? Color.Black : Color.DarkOrange;
        _clearFault.Enabled = s.Mode == FanLoopMode.Faulted;

        SetRow(0, s.CpuTempC, s.CpuRpm, s.CpuTarget, s.CpuAppliedPercent);
        SetRow(1, s.GpuTempC, s.GpuRpm, s.GpuTarget, s.GpuAppliedPercent);
        _boost.Text = s.CoolingBoost ? "Fn+1 Cooling Boost: AÇIK (iki fan %100)" : "Fn+1 Cooling Boost: kapalı";
        _info.Text = $"Profil: {ProfileLabel(s.Profile)}"
            + (s.ManualUntil is { } until ? $" ({until:HH:mm:ss}'e kadar)" : "")
            + $"   EC: {s.EcVersion ?? "?"}   fanlar {(s.HoldsFans ? "serviste" : "EC'de")}"
            + (s.ConsecutiveReadFailures > 0 ? $"   okuma hatası: {s.ConsecutiveReadFailures}" : "");

        int index = Array.FindIndex(Profiles, p => p.Name == s.Profile);
        if (!_profileDirty && index >= 0 && !_profile.DroppedDown) _profile.SelectedIndex = index;

        var profile = s.Profile == "Manual" && s.CpuTarget is { } mc && s.GpuTarget is { } mg
            ? FanProfile.Manual(mc, mg)
            : FanProfile.FindBuiltIn(s.Profile);
        _curve.Show(profile, Point(s.CpuTempC, s.CpuAppliedPercent), Point(s.GpuTempC, s.GpuAppliedPercent));
    }

    private void SetRow(int row, int? tempC, int? rpm, int? target, int? applied)
    {
        _cells[row, 0].Text = tempC is { } t ? $"{t}°C" : "–";
        _cells[row, 1].Text = rpm?.ToString() ?? "–";
        _cells[row, 2].Text = target is { } p ? $"%{p}" : "EC";
        _cells[row, 3].Text = applied is { } a ? $"%{a}" : "–";
        _cells[row, 0].ForeColor = tempC >= HotTracker.HotC ? Color.Firebrick : tempC >= 65 ? Color.DarkOrange : Color.Black;
    }

    private void RenderEvents(IReadOnlyList<FanEventDto> events)
    {
        _events.BeginUpdate();
        _events.Items.Clear();
        foreach (var e in events.Reverse()) _events.Items.Add($"{e.At:HH:mm:ss}  {e.Message}");
        _events.EndUpdate();
    }

    private void ShowMessage(string text, bool ok)
    {
        _message.Text = $"{DateTime.Now:HH:mm:ss}  {text}";
        _message.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
    }

    private static string ProfileLabel(string name)
        => name == "Manual" ? "Manuel" : Profiles.FirstOrDefault(p => p.Name == name).Label ?? name;

    private static (int C, int P)? Point(int? c, int? p) => c is { } cc && p is { } pp ? (cc, pp) : null;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
