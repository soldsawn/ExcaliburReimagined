using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Excalibur.App;
using Excalibur.Core.Fan;
using Excalibur.Core.History;
using Excalibur.Linux.Ui.Controls;

namespace Excalibur.Linux.Ui.Pages;

/// <summary>
/// Geçmiş, as on Windows: temperatures and fan speeds over the last 15 minutes to week, from the service's history
/// (recorded once a second, kept seven days), with a summary of the chosen range under the chart.
/// Up to <see cref="ShortSpan"/> every recorded second is drawn and the chart refreshes every few seconds.
/// </summary>
internal sealed class HistoryPage : UserControl
{
    // About one point per two pixels of a wide chart.
    private const int MaxPoints = 720;
    private static readonly TimeSpan ShortSpan = TimeSpan.FromMinutes(30);
    private static readonly (TimeSpan Span, string Label)[] Ranges =
    [
        (TimeSpan.FromMinutes(15), "15 dk"), (TimeSpan.FromMinutes(30), "30 dk"),
        (TimeSpan.FromHours(1), "1 saat"), (TimeSpan.FromHours(6), "6 saat"),
        (TimeSpan.FromHours(24), "24 saat"), (TimeSpan.FromDays(7), "7 gün"),
    ];

    private readonly FanConnection _connection;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly TextBlock _cpu, _gpu, _note;
    private TimeSpan _span = TimeSpan.FromHours(1);
    private bool _busy;

    internal HistoryChart Chart { get; } = new() { Height = 420, Margin = new Thickness(0, 4, 0, 0) };

    public HistoryPage(FanConnection connection)
    {
        _connection = connection;
        _cpu = Kit.Text("", "Body").Res(TextBlock.ForegroundProperty, "Value");
        _gpu = Kit.Text("", "Body", top: 4).Res(TextBlock.ForegroundProperty, "Ok");
        _note = Kit.Text("", "Caption", top: 8);

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        string group = "historyrange" + GetHashCode();
        foreach (var (span, label) in Ranges)
        {
            var b = Kit.Segment(label, group);
            b.Margin = new Thickness(0, 0, 8, 0);
            b.MinWidth = 90;
            b.Height = 34;
            b.IsChecked = span == _span;
            b.IsCheckedChanged += async (_, _) =>
            {
                if (b.IsChecked != true) return;
                _span = span;
                _refresh.Interval = span <= ShortSpan ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30);
                await Refresh();
            };
            buttons.Children.Add(b);
        }

        Content = new StackPanel
        {
            Children =
            {
                Kit.Text("Geçmiş", "PageTitle"),
                Kit.Text("Sıcaklıkların ve fan hızlarının zaman içindeki seyri. Kayıt yedi gün saklanır.", "Caption", top: 2, bottom: 18),
                Kit.Card("Sıcaklık ve fan", buttons, Chart,
                    Kit.Text("Çizgi ortalamayı, açık renkli alan o aralıktaki en yüksek sıcaklığı gösterir.", "Caption", top: 6)),
                Kit.Card("Seçilen aralıkta", new StackPanel { Children = { _cpu, _gpu } }, _note),
            },
        };

        _refresh.Tick += async (_, _) => await Refresh();
        AttachedToVisualTree += async (_, _) => { _refresh.Start(); await Refresh(); };
        DetachedFromVisualTree += (_, _) => _refresh.Stop();
    }

    private async Task Refresh()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var series = await _connection.HistoryAsync(_span, _span <= ShortSpan ? (int)_span.TotalSeconds : MaxPoints);
            Chart.Show(series);
            Summarize(series is null ? null : HistorySummary.Of(series), series is null);
        }
        finally { _busy = false; }
    }

    private void Summarize(HistorySummary? summary, bool unreachable)
    {
        string Line(FanId fan, ChipSummary c) =>
            $"{Texts.Chip(fan)}: en yüksek {c.Max} °C · ortalama {c.Average} °C · " +
            (c.Hot > TimeSpan.Zero ? $"{HistorySummary.HotC} °C üstünde {Texts.Duration(c.Hot)}" : $"{HistorySummary.HotC} °C'ye çıkmadı");
        _cpu.Text = summary is null ? "" : Line(FanId.Cpu, summary.Cpu);
        _gpu.Text = summary is null ? "" : Line(FanId.Gpu, summary.Gpu);
        _note.Text = unreachable ? "Geçmiş alınamadı: fan servisine ulaşılamıyor ya da kayıt Ayarlar'dan kapatılmış."
            : summary is null ? "Bu aralıkta kayıt yok."
            : "";
    }
}
