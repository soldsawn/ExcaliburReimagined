using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Excalibur.Core.Settings;

namespace Excalibur.App.Ui.Pages;

/// <summary>Simple text pages: the planned features of pages not built yet, and Settings/About.</summary>
internal static class InfoPage
{
    public static UserControl Planned(string title, string intro, string[] items)
    {
        var list = new StackPanel();
        foreach (string item in items)
            list.Children.Add(new DockPanel
            {
                Margin = new Thickness(0, 0, 0, 8),
                Children =
                {
                    Text("•", "Body", accent: true, width: 18),
                    Text(item, "Body"),
                },
            });

        return Page(title, "Bu bölüm hazırlanıyor.",
            Card("Neler gelecek", Text(intro, "Caption", bottom: 12), list));
    }

    public static UserControl Settings(FanConnection connection)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        string? fanFirmware = connection.Status?.EcVersion;
        return Page("Ayarlar", "Uygulamanın görünümü ve hakkında bilgiler.",
            Card("Görünüm",
                Text("Tema Windows'un açık/koyu ayarını izliyor, vurgu rengi kırmızı.", "Body"),
                Text("Yakında: vurgu rengi ve tema seçimi, tepsi simgesindeki rakamların yazı tipi, hızlı panelde görünecek profiller.", "Caption", top: 6)),
            BatteryPage.IntervalCard(connection),
            WindowsPowerCard.Create(),
            Card("Hakkında",
                Text($"Excalibur Reimagined {version}", "Body"),
                Text("Casper Excalibur G870 için, Control Center'ın yerine geçen hafif bir uygulama. Kendiliğinden internete bağlanmaz, " +
                     "veri göndermez; yalnızca aşağıdaki düğmeye basınca GitHub'da yeni sürüm var mı diye bakar.", "Caption", top: 6),
                Text("Fanları arka planda bir Windows hizmeti yönetir. O hizmet bir sorun yaşarsa ikinci bir koruma hizmeti fanları hemen " +
                     "orijinal ayara bırakır.", "Caption", top: 6),
                Text($"İşlemci: {HardwareNames.Cpu ?? "?"}   ·   Ekran kartı: {HardwareNames.Gpu ?? "?"}" +
                     (HardwareNames.Bios is { } bios ? $"   ·   BIOS: {bios}" : "") +
                     (fanFirmware is null ? "" : $"   ·   Fan denetleyici yazılımı: {fanFirmware}"), "Caption", top: 6),
                UpdateCheck()));
    }

    /// <summary>Asks GitHub for the newest release, only when the button is pressed.</summary>
    private static StackPanel UpdateCheck()
    {
        var button = new Button { Content = "Güncellemeleri denetle", HorizontalAlignment = HorizontalAlignment.Left };
        button.SetResourceReference(FrameworkElement.StyleProperty, "PlainButton");
        var result = Text("", "Caption", top: 8);
        var open = new Button { Content = "İndirme sayfasını aç", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        open.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        string? url = null;
        open.Click += (_, _) =>
        {
            if (url is { Length: > 0 }) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            open.Visibility = Visibility.Collapsed;
            result.Text = "Bakılıyor…";
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"ExcaliburReimagined/{current.ToString(3)}");
                var newest = ReleaseFeed.Newest(await http.GetStringAsync(ReleaseFeed.ApiUrl));
                if (newest is not null && ReleaseFeed.IsNewer(newest, current))
                {
                    result.Text = $"Yeni sürüm var: {newest.Version.ToString(3)} (sende {current.ToString(3)}).";
                    url = newest.Url;
                    open.Visibility = Visibility.Visible;
                }
                else result.Text = "En son sürümü kullanıyorsun.";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                result.Text = "GitHub'a ulaşılamadı; internet bağlantını kontrol edip yeniden dene.";
            }
            button.IsEnabled = true;
        };
        return new StackPanel { Margin = new Thickness(0, 12, 0, 0), Children = { button, result, open } };
    }

    internal static UserControl Page(string title, string subtitle, params UIElement[] cards)
    {
        var panel = new StackPanel();
        panel.Children.Add(Text(title, "PageTitle"));
        panel.Children.Add(Text(subtitle, "Caption", top: 2, bottom: 18));
        foreach (var card in cards) panel.Children.Add(card);
        return new UserControl { Content = panel };
    }

    internal static Border Card(string title, params UIElement[] content)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(title, "CardTitle", bottom: 8));
        foreach (var c in content) stack.Children.Add(c);
        var card = new Border { Child = stack, Margin = new Thickness(0, 0, 0, 14) };
        card.SetResourceReference(FrameworkElement.StyleProperty, "CardBox");
        return card;
    }

    /// <summary>A small (i) that explains a term in a tooltip when hovered.</summary>
    internal static TextBlock Tip(string explanation)
    {
        var t = new TextBlock
        {
            Text = "", FontSize = 13, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Help,
            ToolTip = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 },
        };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        t.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
        ToolTipService.SetInitialShowDelay(t, 150);
        ToolTipService.SetShowDuration(t, 30000);
        return t;
    }

    /// <summary>A text followed by its (i) explanation.</summary>
    internal static StackPanel Labeled(string text, string style, string explanation, double top = 0, double bottom = 0)
    {
        var label = Text(text, style);
        label.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, top, 0, bottom), Children = { label, Tip(explanation) } };
    }

    internal static TextBlock Text(string text, string style, double top = 0, double bottom = 0, bool accent = false, double width = double.NaN)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, bottom), Width = width };
        t.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (accent) t.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        return t;
    }
}
