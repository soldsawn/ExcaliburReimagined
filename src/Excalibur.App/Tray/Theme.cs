using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Excalibur.Core.Fan;
using Microsoft.Win32;

namespace Excalibur.App.Tray;

/// <summary>
/// Colors for the panel, OSD and tray icon. Same family as the user's SawnTask app (dark base,
/// thin-bordered cards, colored numbers) but its own gaming accent: crimson (the user's desk
/// theme) instead of blue, with cyan for live values so the accent never reads as "hot".
/// Follows the Windows app theme (light/dark). Personalization (docs/08 5.8) will replace the accent.
/// </summary>
internal sealed record Theme(Color Back, Color Card, Color CardHover, Color Text, Color SubText, Color Accent, Color AccentText,
    Color Border, Color Value)
{
    public static readonly Theme Dark = new(
        Color.FromArgb(27, 27, 30), Color.FromArgb(37, 37, 41), Color.FromArgb(48, 48, 54),
        Color.FromArgb(237, 237, 237), Color.FromArgb(154, 154, 162),
        Color.FromArgb(215, 30, 52), Color.FromArgb(255, 255, 255), Color.FromArgb(54, 54, 60),
        Color.FromArgb(79, 195, 247));

    public static readonly Theme Light = new(
        Color.FromArgb(243, 243, 245), Color.FromArgb(255, 255, 255), Color.FromArgb(234, 234, 238),
        Color.FromArgb(26, 26, 26), Color.FromArgb(96, 96, 104),
        Color.FromArgb(196, 20, 42), Color.FromArgb(255, 255, 255), Color.FromArgb(218, 218, 224),
        Color.FromArgb(0, 120, 180));

    public static Theme Current
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0 ? Dark : Light;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return Light;
            }
        }
    }

    public bool IsDark => this == Dark;

    /// <summary>Same thresholds as the test window: orange from 65 C, red from the hot rule (80 C).</summary>
    public static Color Temperature(int? tempC, Color normal) =>
        tempC >= HotTracker.HotC ? Bad : tempC >= 65 ? Warn : normal;

    public static readonly Color Ok = Color.FromArgb(108, 195, 108);
    public static readonly Color Warn = Color.FromArgb(240, 160, 48);
    public static readonly Color Bad = Color.FromArgb(232, 72, 85);
    public static readonly Color Off = Color.FromArgb(138, 138, 138);
    /// <summary>Glyph color of the system tray icons: white on a dark taskbar, near-black on a light one.</summary>
    public static Color TaskbarText
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is int light && light != 0 ? Color.FromArgb(26, 26, 26) : Color.White;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return Color.White;
            }
        }
    }
}

internal static class Native
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);

    public const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Windows 11 rounded corners and dark frame; ignored on Windows 10.</summary>
    public static void Round(IntPtr hwnd, bool dark)
    {
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
}
