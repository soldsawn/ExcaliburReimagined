using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Settings;
using Excalibur.Linux.Ui.Controls;
using ThemeMode = Excalibur.Core.Settings.ThemeMode;

namespace Excalibur.Linux.Ui.Pages;

/// <summary>
/// Ayarlar, as on Windows: the Appearance card (theme, accent, number font, card corners, chart motion and stock line, history
/// length) and About (version, hardware, copy info, bug report, update check). Windows' hotkeys, Casper clean-up, tray font and
/// on-screen notice have no Linux counterpart.
/// </summary>
internal static class SettingsPage
{
    private static readonly (ThemeMode Mode, string Label)[] Themes =
        [(ThemeMode.System, "Masaüstünü izle"), (ThemeMode.Dark, "Koyu"), (ThemeMode.Light, "Açık")];
    private static readonly (NumberFont Font, string Label)[] NumberFonts =
        [(NumberFont.Bahnschrift, "Dar"), (NumberFont.SegoeUi, "Normal")];
    private static readonly (HistoryMode Mode, string Label)[] Histories =
    [
        (HistoryMode.Days7, "Son 7 gün"), (HistoryMode.Days30, "Son 30 gün (yaklaşık 6 MB)"),
        (HistoryMode.Hour1, "Yalnız son 1 saat (diske yazılmaz)"), (HistoryMode.Off, "Kapalı"),
    ];

    public static Control Create(FanConnection connection) =>
        Kit.Page("Ayarlar", "Uygulamanın görünümü ve hakkında bilgiler.", Appearance(connection), About(connection));

    // ---- appearance ------------------------------------------------------------------------

    private static Control Appearance(FanConnection connection)
    {
        var store = new UserSettingsStore();
        var s = store.Current;
        void Save(Func<UserSettings, UserSettings> change) => store.Save(change(store.Current));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*") };
        int row = 0;
        void Row(string label, Control content, string? explanation = null)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Control text = explanation is null ? Kit.Text(label, "Body") : Kit.Labeled(label, "Body", explanation);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 0);
            content.Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 0);
            Grid.SetRow(text, row);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            grid.Children.Add(text);
            grid.Children.Add(content);
            row++;
        }

        Row("Tema", Segments("theme", Themes, s.Theme, m => Save(x => x with { Theme = m })));
        var (accentRow, wheelBox) = AccentPicker(s.Accent, hex => Save(x => x with { Accent = hex }));
        Row("Vurgu rengi", accentRow, "Seçili düğmeler, kenar çizgileri ve marka yazısı bu renkte olur. Çok koyu ya da çok açık bir renk, temada okunabilsin diye biraz açılır ya da koyulaşır.");
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(wheelBox, row++);
        Grid.SetColumn(wheelBox, 1);
        grid.Children.Add(wheelBox);
        Row("Büyük sayılar", Segments("numbers", NumberFonts, s.NumberFont, f => Save(x => x with { NumberFont = f })),
            "Paneldeki büyük sıcaklık rakamlarının yazı tipi: dar ya da normal.");

        var checks = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        checks.Children.Add(Check("Kartların köşelerinde vurgu çizgileri", s.CornerMarks, on => Save(x => x with { CornerMarks = on })));
        checks.Children.Add(Check("Soğutma grafiğinde parlayan, yumuşak kayan noktalar", s.Motion, on => Save(x => x with { Motion = on })));
        checks.Children.Add(Check("Soğutma grafiğinde orijinal ayar çizgisi", s.StockLine, on => Save(x => x with { StockLine = on })));

        return Kit.Card("Görünüm",
            Kit.Text("Değişiklikler hemen uygulanır.", "Caption", bottom: 14),
            grid, checks, HistoryRow(connection));
    }

    private static WrapPanel Segments<T>(string group, (T Value, string Label)[] items, T current, Action<T> chosen)
    {
        var panel = new WrapPanel();
        foreach (var (value, label) in items)
        {
            var button = Kit.Segment(label, group);
            button.MinWidth = 110;
            button.Margin = new Thickness(0, 0, 8, 0);
            button.IsChecked = Equals(value, current);
            button.IsCheckedChanged += (_, _) => { if (button.IsChecked == true) chosen(value); };
            panel.Children.Add(button);
        }
        return panel;
    }

    private static CheckBox Check(string text, bool on, Action<bool> changed)
    {
        var box = new CheckBox { Content = text, FontSize = 14, IsChecked = on, Margin = new Thickness(0, 0, 0, 8) };
        box.Click += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    /// <summary>Eight round swatches and "Özel renk", which opens the colour wheel below them.</summary>
    private static (WrapPanel Row, Control Wheel) AccentPicker(string current, Action<string> chosen)
    {
        var row = new WrapPanel();
        var rings = new List<(Ellipse Ring, string Hex)>();
        var wheel = new ColourWheel { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var wheelBox = new Border { Child = wheel, IsVisible = false };

        void Mark(string hex)
        {
            foreach (var (ring, h) in rings)
                ring.Opacity = string.Equals(h, hex, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        foreach (var (hex, name) in AccentColor.Presets)
        {
            var (r, g, b) = AccentColor.Parse(hex);
            var ring = new Ellipse { Width = 34, Height = 34, StrokeThickness = 2 }.Res(Shape.StrokeProperty, "Text");
            var dot = new Ellipse { Width = 24, Height = 24, Fill = new SolidColorBrush(Color.FromRgb(r, g, b)) };
            var swatch = new Grid { Width = 36, Height = 36, Margin = new Thickness(0, 0, 6, 0), Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent };
            ToolTip.SetTip(swatch, name);
            swatch.Children.Add(ring);
            swatch.Children.Add(dot);
            swatch.PointerReleased += (_, _) => { Mark(hex); chosen(hex); };
            rings.Add((ring, hex));
            row.Children.Add(swatch);
        }

        var any = Kit.Button("Özel renk");
        any.Margin = new Thickness(6, 0, 0, 0);
        any.VerticalAlignment = VerticalAlignment.Center;
        any.Click += (_, _) => wheelBox.IsVisible = !wheelBox.IsVisible;
        row.Children.Add(any);

        var (cr, cg, cb) = AccentColor.Parse(UserSettings.IsColor(current) ? current : UserSettings.DefaultAccent);
        wheel.Colour = Color.FromRgb(cr, cg, cb);
        // Picked fires on release: one save per choice, not one per mouse move.
        wheel.Picked += c =>
        {
            string hex = AccentColor.ToHex(c.R, c.G, c.B);
            Mark(hex);
            chosen(hex);
        };
        Mark(current);
        return (row, wheelBox);
    }

    /// <summary>How long the service keeps the fan history (its own setting, not the user file).</summary>
    private static StackPanel HistoryRow(FanConnection connection)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260, Margin = new Thickness(0, 6, 0, 0) }.Classed("ThemedCombo");
        combo.ItemsSource = Histories.Select(h => h.Label).ToList();
        var error = Kit.Text("", "Caption", top: 6).Res(TextBlock.ForegroundProperty, "Bad");
        error.IsVisible = false;
        bool loading = true;
        void Load()
        {
            loading = true;
            var mode = connection.Settings?.History ?? HistoryMode.Days7;
            combo.SelectedIndex = Math.Max(0, Array.FindIndex(Histories, h => h.Mode == mode));
            loading = false;
        }
        Load();
        combo.AttachedToVisualTree += async (_, _) => { await connection.RefreshSettingsAsync(); Load(); };
        combo.SelectionChanged += async (_, _) =>
        {
            if (loading || combo.SelectedIndex < 0) return;
            var r = await connection.RequestAsync(new FanRequest("updateSettings", Settings: new ServiceSettingsPatch(History: Histories[combo.SelectedIndex].Mode)));
            error.Text = r is null ? "Arka plan hizmetine ulaşılamadı." : r.Ok ? "" : "Ayar kaydedilemedi.";
            error.IsVisible = error.Text.Length > 0;
        };
        return new StackPanel
        {
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                Kit.Labeled("Geçmiş kaydı", "Body", "Sıcaklık ve fan geçmişinin ne kadar süre saklanacağı. Daha uzun kayıt, Geçmiş sayfasında ve eğri incelemesinde daha çok veri demektir; günde yaklaşık 200 KB yer tutar."),
                combo, error,
            },
        };
    }

    // ---- about -----------------------------------------------------------------------------

    private static Control About(FanConnection connection)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        string? fanFirmware = connection.Status?.EcVersion;
        return Kit.Card("Hakkında",
            Kit.Text($"Excalibur Reimagined {version} (Linux)", "Body"),
            Kit.Text("Casper Excalibur için, Control Center'ın yerine geçen hafif bir uygulama. Kendiliğinden internete bağlanmaz, " +
                     "veri göndermez; yalnızca aşağıdaki düğmeye basınca GitHub'da yeni sürüm var mı diye bakar.", "Caption", top: 6),
            Kit.Text("Fanları arka planda bir sistem hizmeti (excalibur-reimagined) yönetir. Hizmet durur ya da çökerse fanlar hemen " +
                     "dizüstünün kendi ayarına bırakılır.", "Caption", top: 6),
            Kit.Text($"İşlemci: {HardwareNames.Cpu ?? "?"}   ·   Ekran kartı: {HardwareNames.Gpu ?? "?"}" +
                     (HardwareNames.Bios is { } bios ? $"   ·   BIOS: {bios}" : "") +
                     (fanFirmware is null ? "" : $"   ·   Fan denetleyici yazılımı: {fanFirmware}"), "Caption", top: 6),
            Kit.Text(connection.Status is { Mode: FanLoopMode.Running } ? FanPage.WritePathLine : "", "Caption", top: 6),
            CopyInfo(connection),
            UpdateCheck());
    }

    /// <summary>The same one-line summary as the Panel's trial card, for a bug report or a GitHub issue.</summary>
    private static StackPanel CopyInfo(FanConnection connection)
    {
        var button = Kit.Button("Bilgileri kopyala");
        button.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(button)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(OverviewPage.SupportLine(connection.Status));
                button.Content = "Kopyalandı";
            }
        };
        var report = Kit.Button("Sorun bildir");
        report.Margin = new Thickness(8, 0, 0, 0);
        report.Click += async (_, _) => await Report(report, connection);
        return new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 4),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { button, report } },
                Kit.Text("\"Bilgileri kopyala\" model, BIOS ve fan yazılımı sürümünü tek satırda kopyalar. \"Sorun bildir\" son günlerin " +
                         "günlüklerini ana klasörünüze bir zip olarak kaydeder ve GitHub'da sorun sayfasını açar; zip'i oraya sürüklemeniz yeterli. " +
                         "Hiçbir şey kendiliğinden gönderilmez.", "Caption", top: 6),
            },
        };
    }

    /// <summary>Saves the report zip to the home folder, opens it in the file manager and a filled-in GitHub issue (BugReport).</summary>
    private static async Task Report(Button owner, FanConnection connection)
    {
        if (!await ConfirmDialog.Ask(owner, "Sorun bildir",
                $"Son {BugReport.Days} günün fan günlükleri, ayarlar ve sürüm bilgileri ana klasörünüze bir zip olarak kaydedilir. " +
                "Kullanıcı adınız ve klasör yollarınız dosyalardan çıkarılır. Sonra GitHub'da sorun sayfası açılır; neyin çalışmadığını yazıp " +
                "zip'i sayfaya sürükleyin.", "Zip oluştur"))
            return;
        try
        {
            string summary = OverviewPage.SupportLine(connection.Status);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string zip = BugReport.Create(home, connection.Paths.Data, summary, [], DateTime.Now, connection.Paths.Logs);
            Open(System.IO.Path.GetDirectoryName(zip)!);
            Open(BugReport.IssueLink(summary));
            owner.Content = "Zip ana klasörde";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            owner.Content = "Oluşturulamadı";
            ToolTip.SetTip(owner, ex.Message);
        }
    }

    /// <summary>Opens a folder or a web page with the desktop's default program.</summary>
    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", target) { UseShellExecute = false }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>Asks GitHub for the newest release, only when the button is pressed.</summary>
    private static StackPanel UpdateCheck()
    {
        var button = Kit.Button("Güncellemeleri denetle");
        button.HorizontalAlignment = HorizontalAlignment.Left;
        var result = Kit.Text("", "Caption", top: 8);
        var open = Kit.Button("İndirme sayfasını aç", "AccentButton");
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 8, 0, 0);
        open.IsVisible = false;
        string? url = null;
        open.Click += (_, _) => { if (url is { Length: > 0 }) Open(url); };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            open.IsVisible = false;
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
                    open.IsVisible = true;
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
}
