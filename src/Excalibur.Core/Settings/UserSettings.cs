using System.Text.Json;
using System.Text.Json.Serialization;
using Excalibur.Core.Automation;

namespace Excalibur.Core.Settings;

/// <summary>How the tray icon draws the temperature (docs/09: Bahnschrift by default, the pixel sets as options).</summary>
public enum TrayFont
{
    Bahnschrift,
    PixelThin,
    PixelBold,
}

public enum ThemeMode
{
    /// <summary>Follow the Windows app theme (light/dark).</summary>
    System,
    Dark,
    Light,
}

/// <summary>
/// Switches the laptop screen's refresh rate when the charger is plugged in or out (docs/08 3.1).
/// Off by default. 0 means "the highest rate the screen offers" (on the wall) and is invalid on battery.
/// </summary>
public sealed record RefreshRateRule
{
    public bool Enabled { get; init; }
    public int OnAcHz { get; init; }
    public int OnBatteryHz { get; init; } = 60;

    public bool IsValid => (OnAcHz == 0 || OnAcHz is >= 30 and <= 360) && OnBatteryHz is >= 30 and <= 360;
}

/// <summary>
/// Per-user appearance settings of the app (tray and main window), in
/// %AppData%\ExcaliburReimagined\settings.json. They never reach the service or the EC, so the
/// apps write them directly. The personalization page (docs/08 5.8) edits them.
/// </summary>
public sealed record UserSettings
{
    public const int CurrentVersion = 1;
    public const string DefaultAccent = "#D71E34";

    public int Version { get; init; } = CurrentVersion;
    public TrayFont TrayFont { get; init; } = TrayFont.Bahnschrift;
    /// <summary>The on-screen notice for profile changes made outside the main window.</summary>
    public bool Osd { get; init; } = true;
    /// <summary>Accent color as #RRGGBB.</summary>
    public string Accent { get; init; } = DefaultAccent;
    public ThemeMode Theme { get; init; } = ThemeMode.System;
    /// <summary>The screen refresh rate on the charger and on battery; applied by the tray.</summary>
    public RefreshRateRule RefreshRate { get; init; } = new();
    /// <summary>What runs while a program is open (A6); the tray watches the programs and applies them.</summary>
    public IReadOnlyList<AppRule> AppRules { get; init; } = [];
    /// <summary>Windows' own power mode follows the performance mode; off by default (many set the Windows side themselves).</summary>
    public bool WindowsPowerMode { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Reads the file's JSON; anything unreadable or invalid falls back to the default. Never throws.</summary>
    public static UserSettings Parse(string json)
    {
        UserSettings? s;
        try { s = JsonSerializer.Deserialize<UserSettings>(json, Json); }
        catch (JsonException) { return new UserSettings(); }
        if (s is null) return new UserSettings();
        var d = new UserSettings();
        return s with
        {
            TrayFont = Enum.IsDefined(s.TrayFont) ? s.TrayFont : d.TrayFont,
            Theme = Enum.IsDefined(s.Theme) ? s.Theme : d.Theme,
            Accent = IsColor(s.Accent) ? s.Accent : d.Accent,
            RefreshRate = s.RefreshRate is { IsValid: true } ? s.RefreshRate : d.RefreshRate,
            AppRules = CleanRules(s.AppRules),
        };
    }

    /// <summary>A bad or repeated rule is dropped; the others stay.</summary>
    private static IReadOnlyList<AppRule> CleanRules(IReadOnlyList<AppRule?>? rules)
    {
        var result = new List<AppRule>();
        foreach (var r in rules ?? [])
        {
            if (r is null || r.Validate().Count > 0 || result.Count >= AppRule.MaxRules
                || result.Any(x => string.Equals(x.Program, r.Program, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(r);
        }
        return result;
    }

    // Value equality over the rule list (a record would compare it by reference), so a reload that changed nothing stays quiet.
    public bool Equals(UserSettings? other) =>
        other is not null && Version == other.Version && TrayFont == other.TrayFont && Osd == other.Osd && Accent == other.Accent
        && Theme == other.Theme && RefreshRate == other.RefreshRate && AppRules.SequenceEqual(other.AppRules)
        && WindowsPowerMode == other.WindowsPowerMode;

    public override int GetHashCode() => HashCode.Combine(Version, TrayFont, Osd, Accent, Theme, RefreshRate, AppRules.Count);

    public static bool IsColor(string? text) =>
        text is { Length: 7 } && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit);
}

/// <summary>
/// Reads and writes <see cref="UserSettings"/>; raises <see cref="Changed"/> when the file
/// changes (another process of the app saved it), so the tray follows the main window.
/// </summary>
public sealed class UserSettingsStore : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;

    public UserSettingsStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExcaliburReimagined", "settings.json");
        Current = Read();
        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(Path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            // Saves arrive as several events (temp file, replace); act once they settle.
            FileSystemEventHandler onChange = (_, _) => _debounce.Change(200, Timeout.Infinite);
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Renamed += (_, _) => _debounce.Change(200, Timeout.Infinite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public string Path { get; }
    public UserSettings Current { get; private set; }

    /// <summary>Raised on a thread-pool thread after the file changed; marshal to the UI thread.</summary>
    public event Action<UserSettings>? Changed;

    /// <summary>Saves (atomically). Returns false when the file could not be written.</summary>
    public bool Save(UserSettings settings)
    {
        Current = settings with { Version = UserSettings.CurrentVersion };
        try
        {
            AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(Current, UserSettings.Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Reload()
    {
        var next = Read();
        if (next == Current) return;
        Current = next;
        Changed?.Invoke(next);
    }

    private UserSettings Read()
    {
        try { return UserSettings.Parse(File.ReadAllText(Path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new UserSettings(); }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
