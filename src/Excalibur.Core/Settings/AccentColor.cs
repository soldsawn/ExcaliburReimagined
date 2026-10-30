using System.Globalization;

namespace Excalibur.Core.Settings;

/// <summary>
/// The accent colors offered in Settings and the fix that keeps any chosen color readable: a color too dark or too pale for
/// the theme has its lightness pulled into the theme's range (hue and saturation kept), so text and lines on the accent and
/// the accent on the background both stay visible. Colors are "#RRGGBB" as <see cref="UserSettings.Accent"/>.
/// </summary>
public static class AccentColor
{
    public static readonly (string Hex, string Name)[] Presets =
    [
        (UserSettings.DefaultAccent, "Kırmızı"),
        ("#E8731A", "Turuncu"),
        ("#D9A400", "Sarı"),
        ("#2EA44F", "Yeşil"),
        ("#17A2B8", "Camgöbeği"),
        ("#2F6FE0", "Mavi"),
        ("#8A4FD8", "Mor"),
        ("#D9469A", "Pembe"),
    ];

    // HSL lightness that reads on each theme's background: the default red (L 0.48) passes on dark and comes out close to
    // the light theme's former darker red (196, 20, 42) on light.
    private const double DarkMin = 0.40, DarkMax = 0.68, LightMin = 0.25, LightMax = 0.45;

    /// <summary>The color as it is shown on a dark or light theme; an invalid text gives the default.</summary>
    public static (byte R, byte G, byte B) Readable(string? hex, bool dark)
    {
        var (r, g, b) = Parse(UserSettings.IsColor(hex) ? hex! : UserSettings.DefaultAccent);
        var (h, s, l) = ToHsl(r, g, b);
        double fixedL = Math.Clamp(l, dark ? DarkMin : LightMin, dark ? DarkMax : LightMax);
        return Math.Abs(fixedL - l) < 1e-9 ? (r, g, b) : FromHsl(h, s, fixedL);
    }

    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    public static (byte R, byte G, byte B) Parse(string hex) =>
        (byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber), byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
         byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));

    private static (double H, double S, double L) ToHsl(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-9) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static (byte R, byte G, byte B) FromHsl(double h, double s, double l)
    {
        if (s < 1e-9) { byte v = To8(l); return (v, v, v); }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        return (To8(Hue(p, q, h + 1 / 3.0)), To8(Hue(p, q, h)), To8(Hue(p, q, h - 1 / 3.0)));

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
    }

    private static byte To8(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
}
