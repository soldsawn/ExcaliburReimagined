using System.Windows;
using System.Windows.Media;
using Excalibur.Core.Settings;
using ThemeMode = Excalibur.Core.Settings.ThemeMode;
using Microsoft.Win32;

namespace Excalibur.App.Ui;

/// <summary>
/// The main window's colors as brush resources (same palette as the tray's
/// <see cref="Tray.Theme"/>): SawnTask's family of dark base and thin-bordered cards, with a
/// crimson accent (or the user's, made readable by <see cref="AccentColor"/>) and cyan live values. Also carries the
/// appearance switches the controls read: NumberFont, CornerMarks, Motion. Rebuilt whenever the user settings change.
/// </summary>
internal static class Palette
{
    public static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Dark or light as the user chose, or as Windows' app theme when they follow it.</summary>
    public static bool IsDark(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => SystemIsDark(),
    };

    public static ResourceDictionary Build(UserSettings settings)
    {
        bool dark = IsDark(settings.Theme);
        var d = new ResourceDictionary();
        void Add(string key, byte r, byte g, byte b)
        {
            var color = Color.FromRgb(r, g, b);
            d[key + "Color"] = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            d[key] = brush;
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
        var (r, g, b) = AccentColor.Readable(settings.Accent, dark);
        Add("Accent", r, g, b);
        // The accent faintly over the card: the selected profile's fill (formerly 74,33,40 dark / 248,222,225 light for red).
        var card = (Color)d["CardColor"];
        double t = dark ? 0.2 : 0.14;
        Add("AccentSoft", Mix(card.R, r, t), Mix(card.G, g, t), Mix(card.B, b, t));
        d["IsDark"] = dark;
        d["NumberFont"] = new FontFamily(settings.NumberFont == NumberFont.SegoeUi ? "Segoe UI" : "Bahnschrift, Segoe UI");
        d["CornerMarks"] = settings.CornerMarks;
        d["Motion"] = settings.Motion;
        return d;

        static byte Mix(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t);
    }

    /// <summary>Same thresholds as the tray: orange from 65 C, red from the hot rule (80 C).</summary>
    public static Brush Temperature(FrameworkElement owner, int? tempC, string normalKey = "Value") =>
        (Brush)owner.FindResource(tempC >= Core.Fan.HotTracker.HotC ? "Bad" : tempC >= 65 ? "Warn" : normalKey);
}
