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

/// <summary>Windows' own power mode (Settings > Power), which Windows applies only while the Balanced plan is active.</summary>
public enum WindowsPowerOverlay
{
    BestEfficiency,
    Balanced,
    BestPerformance,
}

/// <summary>What Windows switches to with one performance mode; a null part is left alone.</summary>
public sealed record WindowsPowerRule(Guid? Plan = null, WindowsPowerOverlay? Mode = null)
{
    public bool IsValid => Mode is null || Enum.IsDefined(Mode.Value);
}

/// <summary>
/// The Windows power plan and power mode that follow the laptop's performance mode (the stock software switches plans too).
/// Off by default. On battery the mode is always Office, so the battery has its own rule.
/// </summary>
public sealed record WindowsPowerRules
{
    public bool Enabled { get; init; }
    public WindowsPowerRule Office { get; init; } = new(Mode: WindowsPowerOverlay.BestEfficiency);
    public WindowsPowerRule Gaming { get; init; } = new(Mode: WindowsPowerOverlay.Balanced);
    public WindowsPowerRule HighPerformance { get; init; } = new(Mode: WindowsPowerOverlay.BestPerformance);
    public WindowsPowerRule Battery { get; init; } = new(Mode: WindowsPowerOverlay.BestEfficiency);

    [JsonIgnore]
    public bool IsValid => Office is { IsValid: true } && Gaming is { IsValid: true } && HighPerformance is { IsValid: true } && Battery is { IsValid: true };

    /// <summary>The rule in force: the battery's on battery, else the performance mode's.</summary>
    public WindowsPowerRule RuleFor(Power.PowerMode mode, bool onBattery) => onBattery ? Battery : mode switch
    {
        Power.PowerMode.Office => Office,
        Power.PowerMode.Gaming => Gaming,
        _ => HighPerformance,
    };
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
    /// <summary>The Windows power plan and power mode for each performance mode; off by default (many set them themselves).</summary>
    public WindowsPowerRules WindowsPower { get; init; } = new();

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
            WindowsPower = s.WindowsPower is { IsValid: true } ? s.WindowsPower : d.WindowsPower,
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
        && WindowsPower == other.WindowsPower;

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
