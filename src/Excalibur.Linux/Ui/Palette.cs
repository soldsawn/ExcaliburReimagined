using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;
using Excalibur.Core.Settings;
using ThemeMode = Excalibur.Core.Settings.ThemeMode;

namespace Excalibur.Linux.Ui;

/// <summary>
/// The window's colours as resources, the same palette as the Windows window (Excalibur.App/Ui/Palette.cs): dark base,
/// thin-bordered cards, the user's accent made readable, cyan live values. Also the appearance switches the controls read
/// (NumberFont, CornerMarks, StockLine) and the embedded fonts. Rebuilt whenever the user settings change.
/// </summary>
internal static class Palette
{
    private const string Fonts = "avares://excalibur-reimagined/Ui/Fonts";

    public static bool SystemIsDark() =>
        Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant != PlatformThemeVariant.Light;

    /// <summary>Dark or light as the user chose, or as the desktop's theme when they follow it.</summary>
    public static bool IsDark(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => SystemIsDark(),
    };

    public static void Apply(IResourceDictionary d, UserSettings settings)
    {
        bool dark = IsDark(settings.Theme);
        if (Application.Current is { } app) app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        void Add(string key, byte r, byte g, byte b)
        {
            var color = Color.FromRgb(r, g, b);
            d[key + "Color"] = color;
            d[key] = new ImmutableSolidColorBrush(color);
        }

        if (dark)
        {
            Add("Bg", 27, 27, 30);
            Add("Sidebar", 22, 22, 25);
            Add("Card", 37, 37, 41);
            Add("CardHover", 48, 48, 54);
            Add("Border", 54, 54, 60);
            Add("Text", 237, 237, 237);
            Add("SubText", 154, 154, 162);
            Add("Value", 79, 195, 247);
        }
        else
        {
            Add("Bg", 243, 243, 245);
            Add("Sidebar", 235, 235, 238);
            Add("Card", 255, 255, 255);
            Add("CardHover", 234, 234, 238);
            Add("Border", 218, 218, 224);
            Add("Text", 26, 26, 26);
            Add("SubText", 96, 96, 104);
            Add("Value", 0, 120, 180);
        }
        Add("Ok", 108, 195, 108);
        Add("Warn", 240, 160, 48);
        Add("Bad", 232, 72, 85);
        Add("Off", 138, 138, 138);
        Add("OnAccent", 255, 255, 255);
        var (ar, ag, ab) = AccentColor.Readable(settings.Accent, dark);
        Add("Accent", ar, ag, ab);
        // The accent faintly over the card: the selected profile's fill.
        var card = (Color)d["CardColor"]!;
        double t = dark ? 0.2 : 0.14;
        Add("AccentSoft", Mix(card.R, ar, t), Mix(card.G, ag, t), Mix(card.B, ab, t));
        // The thin line under the brand: the accent fading out to the right.
        d["BrandLine"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromRgb(ar, ag, ab), 0), new GradientStop(Colors.Transparent, 1) },
        };
        d["IsDark"] = dark;
        d["UiFont"] = new FontFamily($"{Fonts}#Selawik");
        d["IconFont"] = new FontFamily($"{Fonts}#FluentSystemIcons-Regular");
        d["MonoFont"] = new FontFamily($"{Fonts}#Cascadia Mono");
        d["NumberFont"] = settings.NumberFont == NumberFont.SegoeUi ? new FontFamily($"{Fonts}#Selawik") : new FontFamily($"{Fonts}#Barlow Semi Condensed");
        d["CornerMarks"] = settings.CornerMarks;
        d["Motion"] = settings.Motion;
        d["StockLine"] = settings.StockLine;

        static byte Mix(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t);
    }

    /// <summary>Same thresholds as the Windows window: orange from 65 °C, red from the hot rule (80 °C).</summary>
    public static IBrush Temperature(Control owner, int? tempC, string normalKey = "Value") =>
        Brush(owner, tempC >= Core.Fan.HotTracker.HotC ? "Bad" : tempC >= 65 ? "Warn" : normalKey);

    public static IBrush Brush(Control owner, string key) =>
        owner.TryFindResource(key, owner.ActualThemeVariant, out var value) && value is IBrush b ? b : Brushes.Gray;
}
