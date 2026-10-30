using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Excalibur.Core.Battery;
using Excalibur.Core.Fan;
using Excalibur.Core.Lighting;
using Excalibur.Core.Power;

namespace Excalibur.Core.Settings;

/// <summary>How much temperature/fan history the service keeps (docs/09 A2).</summary>
public enum HistoryMode
{
    /// <summary>One sample a second, a compressed file a day, the last 7 days kept (default).</summary>
    Days7,
    /// <summary>The last hour in memory only; nothing written to disk.</summary>
    Hour1,
    Off,
    /// <summary>As <see cref="Days7"/>, the last 30 days kept (about 6 MB).</summary>
    Days30,
}

/// <summary>
/// Machine-wide settings owned by the fan service, in %ProgramData%\ExcaliburReimagined\fan.json.
/// Only the service writes them, and only after <see cref="Validate"/>; the apps change them
/// through the pipe (setProfile, updateSettings). Later steps add their sections here
/// (power automation A3, custom profiles and slots A4, lighting A5), bumping <see cref="Version"/>
/// only when an existing field changes meaning.
/// </summary>
public sealed record ServiceSettings
{
    public const int CurrentVersion = 2;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>The fan profile to apply: a built-in name or the name of one of <see cref="CustomProfiles"/>.</summary>
    public string Profile { get; init; } = FanProfile.Balanced.Name;

    public HistoryMode History { get; init; } = HistoryMode.Days7;

    /// <summary>The user's own profiles (A4), at most <see cref="CustomProfile.MaxProfiles"/>.</summary>
    public IReadOnlyList<CustomProfile> CustomProfiles { get; init; } = [];

    /// <summary>
    /// Which profiles the quick panel and the tray menu offer, in order (A4, at most <see cref="MaxSlots"/>).
    /// Null = the default four: Quiet, Balanced, Performance, EcAuto.
    /// </summary>
    public IReadOnlyList<string>? Slots { get; init; }

    public const int MaxSlots = 4;
    public static readonly IReadOnlyList<string> DefaultSlots = ["Quiet", "Balanced", "Performance", "EcAuto"];
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveSlots => Slots is { Count: > 0 } s ? s : DefaultSlots;

    /// <summary>A built-in or custom profile by name (case-insensitive), ready to run.</summary>
    public FanProfile? FindProfile(string name) =>
        FanProfile.FindBuiltIn(name)
        ?? CustomProfiles.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.ToProfile();

    /// <summary>
    /// The performance mode the user chose for the wall adapter (A3); null = never chosen, the app leaves the
    /// firmware's mode alone. Office on battery is forced and not stored here.
    /// </summary>
    public PowerMode? PowerMode { get; init; }

    /// <summary>What to change when the charger is plugged in or out (off by default).</summary>
    public PowerAutomation PowerAutomation { get; init; } = new();

    /// <summary>The user's own CPU power limits; null = the power mode decides (nothing is written).</summary>
    public CpuLimit? CpuLimit { get; init; }

    /// <summary>The user's GPU power target in watts (<see cref="GpuLimit"/>); null = the power mode decides (nothing is written).</summary>
    public int? GpuLimitWatts { get; init; }

    /// <summary>
    /// EC firmware versions this machine's user tried and that passed (<see cref="EcTrial"/>), trusted beside the built-in
    /// list. Only the service adds to it, after a trial.
    /// </summary>
    public IReadOnlyList<string> TrustedEcVersions { get; init; } = [];

    /// <summary>The trusted versions whose trial passed through the BIOS (WMI SET 0x0205): their fans are written that way.</summary>
    public IReadOnlyList<string> WmiFanEcVersions { get; init; } = [];

    /// <summary>The keyboard light (A5); null = never chosen, the service leaves the light alone.</summary>
    public KeyboardLighting? Lighting { get; init; }

    /// <summary>
    /// Minutes between the battery reads on battery (one of <see cref="BatteryRecorder.IntervalChoices"/>): each read asks the
    /// battery through the EC, so the default is sparse.
    /// </summary>
    public int BatteryReadMinutes { get; init; } = BatteryRecorder.DefaultMinutes;

    /// <summary>The shape of an EC version string (6 ASCII letters or digits, e.g. P008B0).</summary>
    public static bool IsEcVersion(string? v) => v is { Length: 6 } && v.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z');

    // Value equality over the lists (a record would compare them by reference).
    public bool Equals(ServiceSettings? other) =>
        other is not null && Version == other.Version && Profile == other.Profile && History == other.History
        && PowerMode == other.PowerMode && PowerAutomation == other.PowerAutomation && CpuLimit == other.CpuLimit && GpuLimitWatts == other.GpuLimitWatts && Lighting == other.Lighting
        && BatteryReadMinutes == other.BatteryReadMinutes
        && CustomProfiles.SequenceEqual(other.CustomProfiles) && TrustedEcVersions.SequenceEqual(other.TrustedEcVersions)
        && WmiFanEcVersions.SequenceEqual(other.WmiFanEcVersions)
        && (Slots is null ? other.Slots is null : other.Slots is not null && Slots.SequenceEqual(other.Slots));

    public override int GetHashCode() => HashCode.Combine(Version, Profile, History, PowerMode, CustomProfiles.Count, Slots?.Count);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Problems that make these settings unusable; empty when they are fine.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (CustomProfiles.Count > CustomProfile.MaxProfiles) errors.Add($"more than {CustomProfile.MaxProfiles} custom profiles");
        foreach (var c in CustomProfiles) errors.AddRange(c.Validate().Select(e => $"profile '{c.Name}': {e}"));
        foreach (var dup in CustomProfiles.GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"two custom profiles are named '{dup.Key}'");
        // Looking a profile up builds its curves, which is only safe once the profiles themselves are valid.
        if (errors.Count == 0)
        {
            if (FindProfile(Profile) is null) errors.Add($"unknown profile '{Profile}'");
            if (Slots is { } slots)
            {
                if (slots.Count is 0 or > MaxSlots) errors.Add($"slots must hold 1-{MaxSlots} profiles");
                if (slots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != slots.Count) errors.Add("a profile appears twice in the slots");
                foreach (var n in slots.Where(n => FindProfile(n) is null)) errors.Add($"unknown profile '{n}' in the slots");
            }
        }
        if (!Enum.IsDefined(History)) errors.Add($"unknown history mode '{History}'");
        if (PowerMode is { } pm && !Enum.IsDefined(pm)) errors.Add($"unknown power mode '{PowerMode}'");
        foreach (var (side, rule) in new[] { ("charger", PowerAutomation.OnAc), ("battery", PowerAutomation.OnBattery) })
        {
            if (rule.Mode is { } rm && !Enum.IsDefined(rm)) errors.Add($"unknown power mode in the {side} rule");
            if (rule.FanProfile is { } fp && errors.Count == 0 && FindProfile(fp) is null) errors.Add($"unknown fan profile '{fp}' in the {side} rule");
        }
        // Gaming and High performance are never used on battery (the stock software does the same).
        if (PowerAutomation.OnBattery.Mode is { } bm && bm != Power.PowerMode.Office) errors.Add("only Office can be used on battery");
        if (CpuLimit is { } limit) errors.AddRange(limit.Validate());
        if (GpuLimitWatts is { } gpu) errors.AddRange(GpuLimit.Validate(gpu));
        foreach (var v in TrustedEcVersions.Concat(WmiFanEcVersions).Where(v => !IsEcVersion(v))) errors.Add($"'{v}' is not an EC version");
        if (Lighting is { } lighting) errors.AddRange(lighting.Validate());
        if (!BatteryRecorder.IntervalChoices.Contains(BatteryReadMinutes)) errors.Add($"battery read interval {BatteryReadMinutes} min is not offered");
        return errors;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Reads any version: v1 (<c>{"Profile":"Balanced"}</c>, no Version) is migrated; fields this
    /// build does not know are ignored; anything invalid falls back to its default. Never throws.
    /// </summary>
    public static (ServiceSettings Settings, IReadOnlyList<string> Problems) Parse(string json)
    {
        var problems = new List<string>();
        JsonObject? node;
        try { node = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException ex) { return (new ServiceSettings(), [$"unreadable settings ({ex.Message}); using defaults"]); }
        if (node is null) return (new ServiceSettings(), ["settings are not a JSON object; using defaults"]);

        var d = new ServiceSettings();
        var customs = ReadCustomProfiles(node, problems);
        var known = d with { CustomProfiles = customs };
        string profile = Get(node, "profile", d.Profile, problems);
        if (known.FindProfile(profile) is { } p) profile = p.Name;
        else { problems.Add($"unknown profile '{profile}'; using {d.Profile}"); profile = d.Profile; }
        var slots = ReadSlots(node, known, problems);

        var history = d.History;
        string historyText = Get(node, "history", d.History.ToString(), problems);
        if (!Enum.TryParse(historyText, ignoreCase: true, out history) || !Enum.IsDefined(history))
        {
            problems.Add($"unknown history mode '{historyText}'; using {d.History}");
            history = d.History;
        }

        PowerMode? powerMode = ReadMode(node, "powerMode", problems);
        var automation = ReadAutomation(node, known, problems);
        var cpuLimit = ReadCpuLimit(node, problems);
        var gpuLimit = ReadGpuLimit(node, problems);
        var trusted = ReadEcVersions(node, "trustedEcVersions", problems);
        var wmiFans = ReadEcVersions(node, "wmiFanEcVersions", problems);
        var lighting = ReadLighting(node, problems);
        int batteryMinutes = Get(node, "batteryReadMinutes", d.BatteryReadMinutes, problems);
        if (!BatteryRecorder.IntervalChoices.Contains(batteryMinutes))
        {
            problems.Add($"battery read interval {batteryMinutes} min is not offered; using {d.BatteryReadMinutes}");
            batteryMinutes = d.BatteryReadMinutes;
        }

        int version = Get(node, "version", 1, problems);
        if (version > CurrentVersion) problems.Add($"settings version {version} is newer than this build ({CurrentVersion}); unknown fields ignored");

        return (new ServiceSettings
        {
            Profile = profile, History = history, PowerMode = powerMode, PowerAutomation = automation,
            CustomProfiles = customs, Slots = slots, CpuLimit = cpuLimit, GpuLimitWatts = gpuLimit, TrustedEcVersions = trusted, WmiFanEcVersions = wmiFans, Lighting = lighting,
            BatteryReadMinutes = batteryMinutes,
        }, problems);
    }

    private static IReadOnlyList<string> ReadEcVersions(JsonObject node, string name, List<string> problems)
    {
        var array = node.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value as JsonArray;
        if (array is null) return [];
        var versions = new List<string>();
        foreach (var item in array)
        {
            string? v = item is JsonValue value && value.TryGetValue(out string? text) ? text : null;
            if (!IsEcVersion(v)) { problems.Add($"{name}: '{v}' is not an EC version; dropped"); continue; }
            if (!versions.Contains(v!)) versions.Add(v!);
        }
        return versions;
    }

    private static PowerMode? ReadMode(JsonObject node, string name, List<string> problems)
    {
        string text = Get(node, name, "", problems);
        if (text.Length == 0) return null;
        if (Enum.TryParse(text, ignoreCase: true, out PowerMode mode) && Enum.IsDefined(mode)) return mode;
        problems.Add($"unknown power mode '{text}'; ignored");
        return null;
    }

    private static KeyboardLighting? ReadLighting(JsonObject node, List<string> problems)
    {
        var token = node.FirstOrDefault(kv => string.Equals(kv.Key, "lighting", StringComparison.OrdinalIgnoreCase)).Value;
        if (token is null) return null;
        KeyboardLighting? lighting = null;
        try { lighting = token.Deserialize<KeyboardLighting>(Json); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        // A bad light setting is dropped: the light is left as the firmware has it.
        if (lighting is null || lighting.Validate().Count > 0) { problems.Add("the keyboard light setting is unusable; the light is left alone"); return null; }
        return lighting;
    }

    private static CpuLimit? ReadCpuLimit(JsonObject node, List<string> problems)
    {
        var token = node.FirstOrDefault(kv => string.Equals(kv.Key, "cpuLimit", StringComparison.OrdinalIgnoreCase)).Value;
        if (token is null) return null;
        CpuLimit? limit = null;
        try { limit = token.Deserialize<CpuLimit>(Json); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        // A bad limit is dropped: the power mode decides, which is always safe.
        if (limit is null || limit.Validate().Count > 0) { problems.Add("the CPU limit is unusable; the power mode decides"); return null; }
        return limit;
    }

    private static int? ReadGpuLimit(JsonObject node, List<string> problems)
    {
        var token = node.FirstOrDefault(kv => string.Equals(kv.Key, "gpuLimitWatts", StringComparison.OrdinalIgnoreCase)).Value;
        if (token is null) return null;
        // A bad limit is dropped: the power mode decides, which is always safe.
        if (token is JsonValue v && v.TryGetValue(out int watts) && GpuLimit.Validate(watts).Count == 0) return watts;
        problems.Add("the GPU limit is unusable; the power mode decides");
        return null;
    }

    private static IReadOnlyList<CustomProfile> ReadCustomProfiles(JsonObject node, List<string> problems)
    {
        var array = node.FirstOrDefault(kv => string.Equals(kv.Key, "customProfiles", StringComparison.OrdinalIgnoreCase)).Value as JsonArray;
        if (array is null) return [];
        var result = new List<CustomProfile>();
        foreach (var item in array)
        {
            CustomProfile? c = null;
            try { c = item.Deserialize<CustomProfile>(Json); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
            // One bad profile must not lose the others (or the file): drop it and say so.
            if (c is null || c.Validate().Count > 0 || result.Count >= CustomProfile.MaxProfiles
                || result.Any(r => string.Equals(r.Name, c.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"a custom profile ('{c?.Name}') is invalid or repeated; dropped");
                continue;
            }
            result.Add(c.Normalized());
        }
        return result;
    }

    private static IReadOnlyList<string>? ReadSlots(JsonObject node, ServiceSettings known, List<string> problems)
    {
        var array = node.FirstOrDefault(kv => string.Equals(kv.Key, "slots", StringComparison.OrdinalIgnoreCase)).Value as JsonArray;
        if (array is null) return null;
        var names = new List<string>();
        foreach (var item in array)
        {
            string? n = item is JsonValue v && v.TryGetValue(out string? text) ? text : null;
            var found = n is null ? null : known.FindProfile(n);
            if (found is null || names.Contains(found.Name, StringComparer.OrdinalIgnoreCase) || names.Count >= MaxSlots)
            {
                problems.Add($"slot '{n}' is unknown, repeated or beyond {MaxSlots}; dropped");
                continue;
            }
            names.Add(found.Name);
        }
        return names.Count == 0 ? null : names;
    }

    private static PowerAutomation ReadAutomation(JsonObject node, ServiceSettings known, List<string> problems)
    {
        var token = node.FirstOrDefault(kv => string.Equals(kv.Key, "powerAutomation", StringComparison.OrdinalIgnoreCase)).Value;
        if (token is null) return new PowerAutomation();
        try
        {
            var automation = token.Deserialize<PowerAutomation>(Json) ?? new PowerAutomation();
            // A bad rule must not lose the whole file: replace each invalid rule by its default.
            var d = new PowerAutomation();
            return automation with
            {
                OnAc = CleanRule(automation.OnAc, d.OnAc, "charger", known, problems),
                OnBattery = CleanRule(automation.OnBattery, d.OnBattery, "battery", known, problems),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            problems.Add($"power automation is unreadable ({ex.Message}); using defaults");
            return new PowerAutomation();
        }
    }

    private static PowerRule CleanRule(PowerRule? rule, PowerRule fallback, string side, ServiceSettings known, List<string> problems)
    {
        rule ??= fallback;
        if (rule.Mode is { } m && (!Enum.IsDefined(m) || (side == "battery" && m != Power.PowerMode.Office)))
        {
            problems.Add($"the {side} rule has an unusable mode; using the default");
            rule = rule with { Mode = fallback.Mode };
        }
        if (rule.FanProfile is { } p && known.FindProfile(p) is null)
        {
            problems.Add($"the {side} rule has an unknown fan profile '{p}'; ignored");
            rule = rule with { FanProfile = null };
        }
        return rule;
    }

    private static T Get<T>(JsonObject node, string name, T fallback, List<string> problems)
    {
        // Case-insensitive: v1 was written with PascalCase names.
        var value = node.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        if (value is null) return fallback;
        try { return value.GetValue<T>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            problems.Add($"'{name}' has the wrong type ({value.ToJsonString()}); using {fallback}");
            return fallback;
        }
    }
}

/// <summary>
/// A partial update sent with <c>updateSettings</c>: only the non-null fields change. Profile
/// changes go through <c>setProfile</c> instead (they also switch the running loop).
/// </summary>
public sealed record ServiceSettingsPatch(HistoryMode? History = null, PowerAutomation? PowerAutomation = null, int? BatteryReadMinutes = null)
{
    public ServiceSettings ApplyTo(ServiceSettings s) => s with
    {
        History = History ?? s.History,
        PowerAutomation = PowerAutomation ?? s.PowerAutomation,
        BatteryReadMinutes = BatteryReadMinutes ?? s.BatteryReadMinutes,
    };
}
