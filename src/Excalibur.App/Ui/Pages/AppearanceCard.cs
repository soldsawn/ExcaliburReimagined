using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Excalibur.App.Ui.Controls;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Settings;
using ThemeMode = Excalibur.Core.Settings.ThemeMode;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Settings' Appearance card (B8): theme, accent, number and tray faces, the on-screen notice, card corners, chart motion
/// (user settings: saved to the file, which the window and the tray follow at once) and how long the history is kept
/// (the service's setting, sent over the pipe).
/// </summary>
internal static class AppearanceCard
{
    private static readonly (ThemeMode Mode, string Label)[] Themes =
        [(ThemeMode.System, "Windows'u izle"), (ThemeMode.Dark, "Koyu"), (ThemeMode.Light, "Açık")];
    private static readonly (NumberFont Font, string Label)[] NumberFonts =
        [(NumberFont.Bahnschrift, "Dar"), (NumberFont.SegoeUi, "Normal")];
    private static readonly (TrayFont Font, string Label)[] TrayFonts =
        [(TrayFont.Bahnschrift, "Dar"), (TrayFont.PixelThin, "İnce piksel"), (TrayFont.PixelBold, "Kalın piksel")];
    private static readonly (HistoryMode Mode, string Label)[] Histories =
    [
        (HistoryMode.Days7, "Son 7 gün"), (HistoryMode.Days30, "Son 30 gün (yaklaşık 6 MB)"),
        (HistoryMode.Hour1, "Yalnız son 1 saat (diske yazılmaz)"), (HistoryMode.Off, "Kapalı"),
    ];

    public static Border Create(FanConnection connection)
    {
        var store = new UserSettingsStore();
        var s = store.Current;
        void Save(Func<UserSettings, UserSettings> change) => store.Save(change(store.Current));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        void Row(string label, UIElement content, string? explanation = null)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            FrameworkElement text = explanation is null ? InfoPage.Text(label, "Body") : InfoPage.Labeled(label, "Body", explanation);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 0);
            ((FrameworkElement)content).Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 0);
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
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(wheelBox, row++);
        Grid.SetColumn(wheelBox, 1);
        grid.Children.Add(wheelBox);

        Row("Büyük sayılar", Segments("numbers", NumberFonts, s.NumberFont, f => Save(x => x with { NumberFont = f })),
            "Paneldeki ve hızlı paneldeki büyük sıcaklık rakamlarının yazı tipi: dar (Bahnschrift) ya da normal (Segoe UI).");
        Row("Tepsi simgesi", Segments("tray", TrayFonts, s.TrayFont, f => Save(x => x with { TrayFont = f })),
            "Görev çubuğundaki simgede sıcaklığın hangi yazıyla gösterileceği.");

        var checks = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        checks.Children.Add(Check("Profil değişince ekranın altında kısa bildirim göster", s.Osd, on => Save(x => x with { Osd = on })));
        checks.Children.Add(Check("Kartların köşelerinde vurgu çizgileri", s.CornerMarks, on => Save(x => x with { CornerMarks = on })));
        checks.Children.Add(Check("Soğutma grafiğinde kayan ve nefes alan noktalar", s.Motion, on => Save(x => x with { Motion = on })));

        return InfoPage.Card("Görünüm",
            InfoPage.Text("Değişiklikler hemen uygulanır; hızlı panel ve menü bir sonraki açılışta yeni görünümü alır.", "Caption", bottom: 14),
            grid, checks, HistoryRow(connection));
    }

    private static WrapPanel Segments<T>(string group, (T Value, string Label)[] items, T current, Action<T> chosen)
    {
        var panel = new WrapPanel();
        foreach (var (value, label) in items)
        {
            var button = new RadioButton { GroupName = group, Content = label, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsChecked = Equals(value, current) };
            button.SetResourceReference(FrameworkElement.StyleProperty, "Segment");
            button.Checked += (_, _) => chosen(value);
            panel.Children.Add(button);
        }
        return panel;
    }

    private static CheckBox Check(string text, bool on, Action<bool> changed)
    {
        var box = new CheckBox { Content = text, FontSize = 14, IsChecked = on, Margin = new Thickness(0, 0, 0, 8) };
        box.SetResourceReference(Control.ForegroundProperty, "Text");
        box.Click += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    /// <summary>Eight round swatches and "any colour", which opens the colour wheel below them.</summary>
    private static (WrapPanel Row, FrameworkElement Wheel) AccentPicker(string current, Action<string> chosen)
    {
        var row = new WrapPanel();
        var rings = new List<(Ellipse Ring, string Hex)>();
        var wheel = new ColourWheel { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var wheelBox = new Border { Child = wheel, Visibility = Visibility.Collapsed };

        void Mark(string hex)
        {
            foreach (var (ring, h) in rings)
                ring.Visibility = string.Equals(h, hex, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Hidden;
        }

        foreach (var (hex, name) in AccentColor.Presets)
        {
            var (r, g, b) = AccentColor.Parse(hex);
            var ring = new Ellipse { Width = 34, Height = 34, StrokeThickness = 2 };
            ring.SetResourceReference(Shape.StrokeProperty, "Text");
            var dot = new Ellipse { Width = 24, Height = 24, Fill = new SolidColorBrush(Color.FromRgb(r, g, b)) };
            var swatch = new Grid { Width = 36, Height = 36, Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, ToolTip = name, Background = Brushes.Transparent };
            swatch.Children.Add(ring);
            swatch.Children.Add(dot);
            swatch.MouseLeftButtonUp += (_, _) => { Mark(hex); chosen(hex); };
            rings.Add((ring, hex));
            row.Children.Add(swatch);
        }

        var any = new Button { Content = "Dilediğin renk", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        any.SetResourceReference(FrameworkElement.StyleProperty, "PlainButton");
        any.Click += (_, _) => wheelBox.Visibility = wheelBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
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
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260, Margin = new Thickness(0, 6, 0, 0) };
        combo.SetResourceReference(FrameworkElement.StyleProperty, "ThemedCombo");
        combo.ItemsSource = Histories.Select(h => h.Label).ToList();
        var error = InfoPage.Text("", "Caption", top: 6);
        error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        error.Visibility = Visibility.Collapsed;
        bool loading = true;
        void Load()
        {
            loading = true;
            var mode = connection.Settings?.History ?? HistoryMode.Days7;
            combo.SelectedIndex = Math.Max(0, Array.FindIndex(Histories, h => h.Mode == mode));
            loading = false;
        }
        Load();
        combo.Loaded += async (_, _) => { await connection.RefreshSettingsAsync(); Load(); };
        combo.SelectionChanged += async (_, _) =>
        {
            if (loading || combo.SelectedIndex < 0) return;
            var r = await connection.RequestAsync(new FanRequest("updateSettings", Settings: new ServiceSettingsPatch(History: Histories[combo.SelectedIndex].Mode)));
            error.Text = r is null ? "Arka plan hizmetine ulaşılamadı." : r.Ok ? "" : "Ayar kaydedilemedi.";
            error.Visibility = error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        };
        return new StackPanel
        {
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                InfoPage.Labeled("Geçmiş kaydı", "Body", "Sıcaklık ve fan geçmişinin ne kadar süre saklanacağı. Daha uzun kayıt, Geçmiş sayfasında ve eğri incelemesinde daha çok veri demektir; günde yaklaşık 200 KB yer tutar."),
                combo, error,
            },
        };
    }
}
