using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Excalibur.App.Ui;

/// <summary>
/// The main window's colors as brush resources (same palette as the tray's
/// <see cref="Tray.Theme"/>): SawnTask's family of dark base and thin-bordered cards, with a
/// crimson accent and cyan live values. Personalization (docs/08 5.8) will rebuild this.
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

    public static ResourceDictionary Build(bool dark)
    {
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
            Add("Accent", 215, 30, 52);
            Add("AccentSoft", 74, 33, 40);
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
            Add("Accent", 196, 20, 42);
            Add("AccentSoft", 248, 222, 225);
            Add("Value", 0, 120, 180);
        }
        Add("Ok", 108, 195, 108);
        Add("Warn", 240, 160, 48);
        Add("Bad", 232, 72, 85);
        Add("Off", 138, 138, 138);
        Add("OnAccent", 255, 255, 255);
        d["IsDark"] = dark;
        return d;
    }

    /// <summary>Same thresholds as the tray: orange from 65 C, red from the hot rule (80 C).</summary>
    public static Brush Temperature(FrameworkElement owner, int? tempC, string normalKey = "Value") =>
        (Brush)owner.FindResource(tempC >= Core.Fan.HotTracker.HotC ? "Bad" : tempC >= 65 ? "Warn" : normalKey);
}
