using System.Globalization;

namespace Excalibur.Core.Lighting;

/// <summary>
/// The keyboard light the user chose (docs/09 A5), kept by the service and sent again at start, after sleep and when the
/// light key changes the level: the firmware cannot be asked what it shows (GET 0x0100 answers 0, checked 2026-10-23).
/// Colours are "#RRGGBB". Static lights each zone in its own colour; Breathing uses <see cref="Left"/> for the whole
/// keyboard; ColorCycle picks its own colours.
/// </summary>
public sealed record KeyboardLighting
{
    public LightEffect Effect { get; init; } = LightEffect.Static;

    /// <summary>0-2, the levels of the light key.</summary>
    public int Brightness { get; init; } = 1;

    public string Left { get; init; } = "#00FFFF";
    public string Middle { get; init; } = "#00FFFF";
    public string Right { get; init; } = "#00FFFF";

    /// <summary>Minutes without keyboard or mouse input before the light goes out; 0 = never.</summary>
    public int IdleOffMinutes { get; init; }

    public const int MaxIdleOffMinutes = 60;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(Effect)) errors.Add($"unknown light effect '{Effect}'");
        if (Brightness is < 0 or > LightSetting.MaxBrightness) errors.Add($"brightness must be 0-{LightSetting.MaxBrightness}");
        foreach (var (name, colour) in new[] { ("left", Left), ("middle", Middle), ("right", Right) })
            if (ParseColour(colour) is null) errors.Add($"the {name} colour '{colour}' is not #RRGGBB");
        if (IdleOffMinutes is < 0 or > MaxIdleOffMinutes) errors.Add($"idle-off minutes must be 0-{MaxIdleOffMinutes}");
        return errors;
    }

    /// <summary>"#RRGGBB" as (r, g, b), or null when it is not one.</summary>
    public static (int R, int G, int B)? ParseColour(string? text)
    {
        if (text is not { Length: 7 } || text[0] != '#'
            || !int.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb)) return null;
        return (rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF);
    }

    /// <summary>
    /// The commands that show this light, in order. Brightness is keyboard-wide and the last command's level wins
    /// (2026-10-23), so every command carries the same level, also the ones that turn the light off.
    /// </summary>
    public IReadOnlyList<(LightZone Zone, LightSetting Setting)> Commands(bool idle)
    {
        LightSetting With(LightEffect effect, string colour)
        {
            var (r, g, b) = ParseColour(colour) ?? (0, 0, 0);
            return new LightSetting(effect, Brightness, r, g, b);
        }
        if (idle || Effect == LightEffect.Off) return [(LightZone.Keyboard, With(LightEffect.Off, "#000000"))];
        if (Effect != LightEffect.Static || (string.Equals(Left, Middle, StringComparison.OrdinalIgnoreCase)
                                             && string.Equals(Left, Right, StringComparison.OrdinalIgnoreCase)))
            return [(LightZone.Keyboard, With(Effect, Left))];
        return
        [
            (LightZone.KeyboardLeft, With(LightEffect.Static, Left)),
            (LightZone.KeyboardMiddle, With(LightEffect.Static, Middle)),
            (LightZone.KeyboardRight, With(LightEffect.Static, Right)),
        ];
    }

    /// <summary>
    /// The light key's event (GMC_WMIEvent): byte 1 = 0, byte 0 = the new level (docs/02 §4.5; the stock daemon then writes
    /// its colours again at that level).
    /// </summary>
    public static bool TryParseKeyEvent(byte[] detail, out int brightness)
    {
        brightness = 0;
        if (detail.Length < 2 || detail[1] != 0) return false;
        brightness = Math.Min((int)detail[0], LightSetting.MaxBrightness);
        return true;
    }
}
