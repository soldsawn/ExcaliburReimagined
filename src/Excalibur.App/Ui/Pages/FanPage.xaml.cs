using System.Windows;
using System.Windows.Controls;
using Excalibur.App.Tray;
using Excalibur.App.Ui.Controls;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;
using System.Windows.Threading;

namespace Excalibur.App.Ui.Pages;

/// <summary>Fan profiles, the curve chart, time-limited manual mode and the service's events.</summary>
public partial class FanPage : UserControl
{
    private static readonly int[] Minutes = [1, 5, 10];
    private readonly FanConnection _connection;
    private int _minutes = Minutes[0];
    private IReadOnlyList<FanEventDto>? _shownEvents;
    // The faded trail: the last 10 minutes, refreshed every 10 s while the page is on screen.
    private readonly DispatcherTimer _trailTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _trailBusy;

    internal FanPage(FanConnection connection)
    {
        _connection = connection;
        InitializeComponent();

        var selector = new ProfileSelector(connection);
        selector.CommandFailed += ShowError;
        ProfileHost.Content = selector;

        foreach (int m in Minutes)
        {
            var b = new RadioButton { Content = $"{m} dk", GroupName = "duration", MinWidth = 64, IsChecked = m == _minutes };
            b.SetResourceReference(StyleProperty, "Segment");
            b.Checked += (_, _) => _minutes = m;
            Durations.Children.Add(b);
        }
        CpuSlider.ValueChanged += (_, _) => SliderTexts();
        GpuSlider.ValueChanged += (_, _) => SliderTexts();
        SliderTexts();

        ApplyManual.Click += async (_, _) => ShowError(await _connection.SendAsync(
            new FanRequest("setManual", Cpu: (int)CpuSlider.Value, Gpu: (int)GpuSlider.Value, Minutes: _minutes)));
        ClearFault.Click += async (_, _) => ShowError(await _connection.SendAsync(new FanRequest("clearFault")));

        _connection.Changed += Render;
        _trailTimer.Tick += async (_, _) => await RefreshTrail();
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible) { _trailTimer.Stop(); return; }
            _trailTimer.Start();
            await RefreshTrail();
        };
        Render();
    }

    private async Task RefreshTrail()
    {
        if (_trailBusy) return;
        _trailBusy = true;
        try { Chart.ShowTrail(await _connection.HistoryAsync(TimeSpan.FromMinutes(10), 300)); }
        finally { _trailBusy = false; }
    }

    private void SliderTexts()
    {
        // The EC runs the fans at about 54 rpm per percent (docs/06).
        CpuSliderText.Text = $"{"%" + CpuSlider.Value.ToString("0"),4} ~{CpuSlider.Value * 54,4:0} rpm";
        GpuSliderText.Text = $"{"%" + GpuSlider.Value.ToString("0"),4} ~{GpuSlider.Value * 54,4:0} rpm";
    }

    private void ShowError(string? error)
    {
        CommandError.Text = error ?? "";
        CommandError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Render()
    {
        var s = _connection.Status;
        bool on = _connection.Connected && s is not null;
        ApplyManual.IsEnabled = on;
        FaultBanner.Visibility = on && s!.Mode == FanLoopMode.Faulted ? Visibility.Visible : Visibility.Collapsed;
        WritePathText.Visibility = on && s!.Mode == FanLoopMode.Running ? Visibility.Visible : Visibility.Collapsed;
        if (on) WritePathText.Text = WritePathLine(s!.WritePath);
        if (!on)
        {
            Chart.Show(null, null);
            CurveTitle.Text = "Eğri";
            return;
        }

        var profile = s!.Profile == "Manual" && s.CpuTarget is { } mc && s.GpuTarget is { } mg
            ? FanProfile.Manual(mc, mg)
            : s.Profile == "EcAuto" ? FanProfile.EcAuto : _connection.Catalog.Find(s.Profile);
        bool separateGpu = profile is { IsEcAuto: false } p && !p.CpuCurve.Points.SequenceEqual(p.GpuCurve.Points);
        GpuLegend.Visibility = separateGpu ? Visibility.Visible : Visibility.Collapsed;
        // Original settings draw no profile curve: the legend's gray line already says which one is theirs.
        CpuLegend.Visibility = profile?.IsEcAuto == true ? Visibility.Collapsed : Visibility.Visible;
        CpuLegendText.Text = separateGpu ? "İşlemci eğrisi" : "Profil eğrisi";
        CurveTitle.Text = $"Eğri — {ProfileNames.Label(s.Profile, s.ManualUntil)}";
        Chart.Show(profile, s);

        if (!ReferenceEquals(_shownEvents, _connection.Events))
        {
            _shownEvents = _connection.Events;
            EventList.ItemsSource = _connection.Events.Reverse()
                .Select(e => new { Time = e.At.ToString("HH:mm:ss"), Message = Texts.Event(e.Message) }).ToList();
        }
    }

    /// <summary>How the fans are written here (the trial chose it on a model other than the G870).</summary>
    public static string WritePathLine(FanWritePath path) => path == FanWritePath.Wmi
        ? $"Fan kontrolü: BIOS üzerinden. Bu dizüstünde fanlar en az %{FanControlLoop.WmiFloor} döner."
        : "Fan kontrolü: doğrudan fan denetleyicisine yazılıyor.";

}
