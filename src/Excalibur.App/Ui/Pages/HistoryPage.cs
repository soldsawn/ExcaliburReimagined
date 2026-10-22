using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Excalibur.App.Ui.Controls;
using Excalibur.Core.Fan;
using Excalibur.Core.History;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Geçmiş: temperatures and fan speeds over the last hour to week, from the service's history
/// (recorded once a second, kept seven days), with a summary of the chosen range under the chart.
/// </summary>
internal sealed class HistoryPage : UserControl
{
    // About one point per two pixels of a wide chart.
    private const int MaxPoints = 720;
    private static readonly (TimeSpan Span, string Label)[] Ranges =
    [
        (TimeSpan.FromHours(1), "1 saat"), (TimeSpan.FromHours(6), "6 saat"),
        (TimeSpan.FromHours(24), "24 saat"), (TimeSpan.FromDays(7), "7 gün"),
    ];

    private readonly FanConnection _connection;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly TextBlock _cpu, _gpu, _note;
    private TimeSpan _span = Ranges[0].Span;
    private bool _busy;

    internal HistoryChart Chart { get; } = new() { Height = 420, Margin = new Thickness(0, 4, 0, 0) };

    public HistoryPage(FanConnection connection)
    {
        _connection = connection;
        _cpu = InfoPage.Text("", "Body");
        _cpu.SetResourceReference(TextBlock.ForegroundProperty, "Value");
        _gpu = InfoPage.Text("", "Body", top: 4);
        _gpu.SetResourceReference(TextBlock.ForegroundProperty, "Ok");
        _note = InfoPage.Text("", "Caption", top: 8);

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        string group = "historyrange" + GetHashCode();
        foreach (var (span, label) in Ranges)
        {
            var b = new RadioButton { GroupName = group, Content = label, Margin = new Thickness(0, 0, 8, 0), MinWidth = 90, Height = 34, IsChecked = span == _span };
            b.SetResourceReference(StyleProperty, "Segment");
            b.Checked += async (_, _) => { _span = span; await Refresh(); };
            buttons.Children.Add(b);
        }

        Content = new StackPanel
        {
            Children =
            {
                InfoPage.Text("Geçmiş", "PageTitle"),
                InfoPage.Text("Sıcaklıkların ve fan hızlarının zaman içindeki seyri. Kayıt yedi gün saklanır.", "Caption", top: 2, bottom: 18),
                InfoPage.Card("Sıcaklık ve fan", buttons, Chart,
                    InfoPage.Text("Çizgi ortalamayı, açık renkli alan o aralıktaki en yüksek sıcaklığı gösterir.", "Caption", top: 6)),
                InfoPage.Card("Seçilen aralıkta", _cpu, _gpu, _note),
            },
        };

        _refresh.Tick += async (_, _) => await Refresh();
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible) { _refresh.Stop(); return; }
            _refresh.Start();
            await Refresh();
        };
    }

    private async Task Refresh()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var series = await _connection.HistoryAsync(_span, MaxPoints);
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
