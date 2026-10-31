namespace Excalibur.Core.Settings;

/// <summary>
/// The global shortcuts the tray registers (docs/08 5.2): cycle the fan profile, cycle the power mode, open the window. Each
/// is text such as "Ctrl+Alt+F" (see <see cref="Hotkey"/>); empty turns that one off.
/// </summary>
public sealed record HotkeySettings
{
    public bool Enabled { get; init; } = true;
    public string Profile { get; init; } = "Ctrl+Alt+F";
    public string PowerMode { get; init; } = "Ctrl+Alt+M";
    public string Window { get; init; } = "Ctrl+Alt+E";

    /// <summary>Unreadable entries become empty (off) rather than failing the whole settings file.</summary>
    public HotkeySettings Cleaned() => this with { Profile = Hotkey.Clean(Profile), PowerMode = Hotkey.Clean(PowerMode), Window = Hotkey.Clean(Window) };
}

/// <summary>A key combination as Windows' RegisterHotKey takes it: MOD_* flags and a virtual-key code.</summary>
public readonly record struct Hotkey(uint Modifiers, uint Key)
{
    public const uint Alt = 0x1, Control = 0x2, Shift = 0x4, Win = 0x8;

    /// <summary>"Ctrl+Alt+F": at least Ctrl, Alt or Win, then one letter, digit or F1-F24.</summary>
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0, key = 0;
        foreach (var raw in text.Split('+'))
        {
            string part = raw.Trim();
            switch (part.ToUpperInvariant())
            {
                case "CTRL": mods |= Control; continue;
                case "ALT": mods |= Alt; continue;
                case "SHIFT": mods |= Shift; continue;
                case "WIN": mods |= Win; continue;
            }
            if (key != 0 || KeyCode(part) is not { } k) return null;
            key = k;
        }
        return key != 0 && (mods & (Control | Alt | Win)) != 0 ? new Hotkey(mods, key) : null;
    }

    /// <summary>The same text Parse reads, modifiers in a fixed order.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & Alt) != 0) parts.Add("Alt");
        if ((Modifiers & Shift) != 0) parts.Add("Shift");
        if ((Modifiers & Win) != 0) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    /// <summary>A valid combination in its normal spelling, or empty.</summary>
    public static string Clean(string? text) => Parse(text)?.ToString() ?? "";

    /// <summary>Virtual-key code of a letter (A-Z), digit (0-9) or function key (F1-F24).</summary>
    public static uint? KeyCode(string name)
    {
        name = name.Trim().ToUpperInvariant();
        if (name.Length == 1 && name[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') return name[0];
        if (name.Length is 2 or 3 && name[0] == 'F' && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        return null;
    }

    public static string KeyName(uint key) => key is >= 0x70 and <= 0x87 ? $"F{key - 0x70 + 1}" : ((char)key).ToString();
}
