using System.Drawing;
using System.Windows.Forms;

namespace Excalibur.App.Tray;

/// <summary>Built-in profiles in the order the tray shows them, with their Turkish labels.</summary>
internal static class ProfileNames
{
    public static readonly (string Name, string Label, string Short)[] All =
    [
        ("Quiet", "Sessiz", "Sessiz"),
        ("Balanced", "Dengeli", "Dengeli"),
        ("Performance", "Performans", "Performans"),
        ("EcAuto", "Orijinal ayar", "Orijinal"),
    ];

    public static string Label(string name, DateTimeOffset? manualUntil = null) => name switch
    {
        "Manual" => manualUntil is { } until ? $"Manuel ({until:HH:mm}'e kadar)" : "Manuel",
        _ => All.FirstOrDefault(p => p.Name == name).Label ?? name,
    };
}
