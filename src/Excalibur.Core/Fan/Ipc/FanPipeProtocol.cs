using System.Text.Json;
using System.Text.Json.Serialization;
using Excalibur.Core.Battery;
using Excalibur.Core.History;
using Excalibur.Core.Lighting;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.Core.Fan.Ipc;

/// <summary>
/// The fan service's local control channel: a named pipe carrying one JSON request line
/// and one JSON response line per exchange. Shared by the service and its clients (UI).
/// </summary>
public static class FanPipe
{
    /// <summary>
    /// 1: status, events, setProfile, setManual, release, clearFault.
    /// 2: + request Source, getSettings, updateSettings, status ProtocolVersion / ProfileChangedBy / ProfileChangedAt.
    /// 3: + history(from, to, maxPoints), status CpuReason / GpuReason / CpuCurvePercent / GpuCurvePercent.
    /// 4: + setPowerMode(powerMode), status Power, response Code (stable error code), settings PowerMode / PowerAutomation.
    /// 5: + listProfiles, saveProfile(definition), deleteProfile(profile), setSlots(names); response Profiles / Slots; any command that takes a
    ///    profile name accepts custom profile names too.
    /// 6: + status Power CpuWatts / CpuPl1Watts / CpuPl2Watts / GpuPowerWatts / CpuLimit, setCpuLimit(cpuLimit) (none = the power mode decides),
    ///    response Code cpu_limit_invalid / cpu_limit_failed.
    /// 7: + trialEc (user-approved trial of an unverified EC firmware; response Code trial_unavailable), status Trial,
    ///    settings TrustedEcVersions.
    /// 8: + setLighting(lighting) (keyboard light; stored and shown), setLightIdle(idle) (the tray saw no input for the
    ///    idle-off time / input again), settings Lighting, status LightIdleOffMinutes / LightIdle / LightBrightness.
    /// 9: + status EcSeenElsewhere (an unverified EC version whose trial passed on another machine).
    /// 10: + status Battery (charge, state, Windows' time left; health once read; the last average on battery),
    ///     settings BatteryReadMinutes.
    /// New fields are optional both ways, so an older client still works against a newer service.
    /// </summary>
    public const int ProtocolVersion = 10;

    public const string Name = "ExcaliburFan";
    /// <summary>The --simulate instance, so it can run beside the real service.</summary>
    public const string SimName = "ExcaliburFanSim";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Who sent a command; the tray uses it to decide whether to show its on-screen notice.</summary>
public static class FanSource
{
    public const string Tray = "tray";
    public const string Window = "window";
    public const string Automation = "automation";
    /// <summary>The service itself (e.g. a timed manual mode ending).</summary>
    public const string Service = "service";
}

/// <summary>
/// Commands: status, events, setProfile(profile), setManual(cpu, gpu, minutes), release, clearFault,
/// getSettings, updateSettings(settings), history(from, to, maxPoints), setPowerMode(powerMode),
/// shutdown (stops the guardian and this service; fans go back to the EC),
/// listProfiles, saveProfile(definition), deleteProfile(profile), setSlots(names), setCpuLimit(cpuLimit), trialEc,
/// setLighting(lighting), setLightIdle(idle).
/// <see cref="Source"/> is one of <see cref="FanSource"/>.
/// </summary>
public sealed record FanRequest(string Cmd, string? Profile = null, int? Cpu = null, int? Gpu = null, int? Minutes = null,
    string? Source = null, ServiceSettingsPatch? Settings = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, int? MaxPoints = null,
    PowerMode? PowerMode = null, CustomProfile? Definition = null, IReadOnlyList<string>? Names = null, CpuLimit? CpuLimit = null,
    KeyboardLighting? Lighting = null, bool? Idle = null);

/// <param name="Code">A stable machine-readable reason when <paramref name="Ok"/> is false and the UI should word it itself
/// (power_battery, power_usbc, power_failed); <paramref name="Error"/> stays an English sentence for logs.</param>
public sealed record FanResponse(bool Ok, string? Error = null, FanStatusDto? Status = null, IReadOnlyList<FanEventDto>? Events = null,
    ServiceSettings? Settings = null, HistorySeries? History = null, string? Code = null,
    IReadOnlyList<ProfileInfoDto>? Profiles = null, IReadOnlyList<string>? Slots = null);

/// <summary>One profile as the editor needs it. Built-ins carry their curves so a copy can start from them; EcAuto has none.</summary>
public sealed record ProfileInfoDto(string Name, bool BuiltIn, IReadOnlyList<CurvePoint>? Cpu, IReadOnlyList<CurvePoint>? Gpu);

/// <summary>
/// Power as the apps show it. <see cref="Mode"/> is what is really active (read from the GPU limit, else what the service last sent);
/// <see cref="Preferred"/> is the user's choice for the wall adapter, null when never chosen.
/// </summary>
public sealed record PowerStatusDto(
    PowerMode? Mode,
    PowerMode? Preferred,
    bool OnBattery,
    int? BatteryPercent,
    bool UsbCAdapter,
    int? GpuWatts,
    PowerLock Lock,
    bool AutomationEnabled,
    int? CpuWatts = null,
    int? CpuPl1Watts = null,
    int? CpuPl2Watts = null,
    int? GpuPowerWatts = null,
    CpuLimit? CpuLimit = null)
{
    public static PowerStatusDto From(PowerStatus s) =>
        new(s.Mode, s.Preferred, s.Source.OnBattery, s.Source.BatteryPercent, s.Source.UsbCAdapter, s.GpuWatts, s.Lock, s.AutomationEnabled,
            s.Cpu?.Watts, s.Cpu?.Pl1Watts, s.Cpu?.Pl2Watts, s.GpuPowerWatts, s.CpuLimit);
}

/// <summary>
/// The battery: charge and state from Windows (always current), health from the battery itself (null until the service
/// has read it, at the earliest 10 minutes after Windows started). <see cref="AverageWatts"/> is the last finished interval of
/// this time on battery (<see cref="BatteryRecorder"/>), null on the wall and until one finishes. Null as a whole without a battery.
/// </summary>
public sealed record BatteryStatusDto(
    int? Percent,
    BatteryState State,
    int? RemainingMinutes,
    int? DesignedMWh = null,
    int? FullMWh = null,
    int? HealthPercent = null,
    string? Chemistry = null,
    int? Cycles = null,
    DateTimeOffset? HealthReadAt = null,
    double? AverageWatts = null,
    int? AverageMinutes = null)
{
    public static BatteryStatusDto? From(BatteryLive? live, BatteryHealth? health, BatteryAverage? average = null) => live is not { } l ? null
        : new(l.Percent, l.State, l.RemainingMinutes,
            health?.DesignedMWh, health?.FullMWh, health?.Percent, health?.Chemistry, health?.Cycles, health?.ReadAt,
            average?.Watts, average is null ? null : (int)Math.Round(average.Span.TotalMinutes));
}

public sealed record FanEventDto(DateTimeOffset At, string Message);

/// <summary>
/// Wire form of <see cref="FanStatus"/>. Fan targets are percents, or null for "EC automatic".
/// </summary>
public sealed record FanStatusDto(
    FanLoopMode Mode,
    string Profile,
    DateTimeOffset? ManualUntil,
    int? CpuTempC,
    int? GpuTempC,
    int? CpuRpm,
    int? GpuRpm,
    int? CpuTarget,
    int? GpuTarget,
    int? CpuAppliedPercent,
    int? GpuAppliedPercent,
    bool CoolingBoost,
    string? EcVersion,
    int ConsecutiveReadFailures,
    bool HoldsFans,
    bool Simulated,
    DateTimeOffset ServiceStartedAt,
    int ProtocolVersion = 1,
    string? ProfileChangedBy = null,
    DateTimeOffset? ProfileChangedAt = null,
    FanReason CpuReason = FanReason.Auto,
    FanReason GpuReason = FanReason.Auto,
    int? CpuCurvePercent = null,
    int? GpuCurvePercent = null,
    PowerStatusDto? Power = null,
    EcTrialStatus? Trial = null,
    int LightIdleOffMinutes = 0,
    bool LightIdle = false,
    int? LightBrightness = null,
    bool EcSeenElsewhere = false,
    BatteryStatusDto? Battery = null)
{
    public static FanStatusDto From(FanStatus s, bool simulated, DateTimeOffset startedAt)
    {
        var snap = s.Snapshot;
        return new FanStatusDto(
            s.Mode, s.Profile, s.ManualUntil,
            snap?.CpuTempC, snap?.GpuTempC, snap?.CpuRpm, snap?.GpuRpm,
            ToPercent(snap?.CpuTarget), ToPercent(snap?.GpuTarget),
            snap?.CpuAppliedPercent, snap?.GpuAppliedPercent,
            snap?.CoolingBoost ?? false,
            s.EcVersion, s.ConsecutiveReadFailures, s.HoldsFans, simulated, startedAt,
            FanPipe.ProtocolVersion, s.ProfileChangedBy, s.ProfileChangedAt,
            s.CpuReason, s.GpuReason, s.CpuCurvePercent, s.GpuCurvePercent, Trial: s.Trial, EcSeenElsewhere: s.EcSeenElsewhere);
    }

    private static int? ToPercent(FanTarget? t) => t is { IsAuto: false } v ? v.Percent : null;
}
