using System.Windows;
using System.Windows.Controls;
using Excalibur.App.Ui.Controls;
using Excalibur.Core.Casper;
using Excalibur.Core.Fan.Ipc;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Settings' "Casper programları" card (A8): what of the Control Center is installed and running, "Kapat" (stop and disable,
/// remembering how it was) and "Geri al". The fan service does the work (it runs as SYSTEM); other fan tools that also use
/// the EC are listed as a warning only.
/// </summary>
internal static class CasperCard
{
    public static Border Create(FanConnection connection)
    {
        var list = new StackPanel();
        var status = InfoPage.Text("Bakılıyor…", "Caption", top: 4);
        var off = new Button { Content = "Kapat", Margin = new Thickness(0, 0, 8, 0) };
        off.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        var restore = new Button { Content = "Geri al" };
        restore.SetResourceReference(FrameworkElement.StyleProperty, "PlainButton");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(off);
        buttons.Children.Add(restore);
        off.IsEnabled = restore.IsEnabled = false;

        void Show(FanResponse? r)
        {
            list.Children.Clear();
            if (r?.Casper is not { } report)
            {
                status.Text = r is null ? "Fan servisine ulaşılamadı." : "Bu sürümdeki fan servisi Casper programlarını tanımıyor; uygulamayı güncelleyin.";
                off.IsEnabled = restore.IsEnabled = false;
                return;
            }
            foreach (var item in report.Items.Where(i => i.Kind != CasperKind.Warning))
                list.Children.Add(InfoPage.Text($"• {Describe(item)}: {(item.Disabled ? "kapalı" : "açık")}", "Body", top: 4));
            foreach (var item in report.Items.Where(i => i.Kind == CasperKind.Warning))
                list.Children.Add(InfoPage.Text($"⚠ {item.Name}: fan denetleyicisini kullanan başka bir program{(item.Disabled ? " (kapalı)" : "")}. " +
                                                "İkisi aynı anda çalışırsa fanlar karışabilir; bu uygulama ona dokunmaz.", "Caption", top: 6));
            bool anyOn = report.Items.Any(i => i.Kind != CasperKind.Warning && !i.Disabled);
            status.Text = report.Error is { } error ? $"Bazı parçalar değiştirilemedi: {error}"
                : !report.HasCasper && !report.CanRestore ? "Casper Control Center kurulu görünmüyor; kapatılacak bir şey yok."
                : anyOn ? "Kapatınca bu parçalar durdurulur ve bilgisayar açılışında başlamaz. Hiçbir şey silinmez; Geri al eski hâline döndürür."
                : "Casper parçaları kapalı. Geri al ile eski hâline döndürebilirsiniz.";
            off.IsEnabled = anyOn;
            restore.IsEnabled = report.CanRestore;
        }

        async Task Send(string cmd)
        {
            off.IsEnabled = restore.IsEnabled = false;
            status.Text = "Bekleyin…";
            Show(await connection.RequestAsync(new FanRequest(cmd, Source: FanSource.Window)));
        }

        off.Click += async (_, _) =>
        {
            if (ConfirmDialog.Ask(off, "Casper programlarını kapat",
                    "Control Center'ın arka plan parçaları durdurulup açılışta başlamayacak şekilde ayarlanır. Klavye ışığı, güç modu ve fanlar " +
                    "bu uygulamadan yönetilmeye devam eder. İstediğiniz zaman Geri al ile eski hâline dönebilirsiniz.", "Kapat"))
                await Send("casperDisable");
        };
        restore.Click += async (_, _) => await Send("casperRestore");
        list.Loaded += async (_, _) => Show(await connection.RequestAsync(new FanRequest("casperScan")));

        return InfoPage.Card("Casper programları",
            InfoPage.Text("Casper Control Center'ın arka planda çalışan parçaları: fan ve donanım sürücüsü, açılış görevleri ve programları. " +
                          "Bu uygulama onların işini yaptığı için kapatılabilirler.", "Caption", bottom: 8),
            list, status, buttons);
    }

    private static string Describe(CasperItem item) => item.Kind switch
    {
        CasperKind.Service when item.Detail.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) => $"Sürücü {item.Name}",
        CasperKind.Service => $"Hizmet {item.Name}",
        CasperKind.Task => $"Açılış görevi {item.Name}",
        CasperKind.Process => $"Program {item.Name}",
        _ => item.Name,
    };
}
