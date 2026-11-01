using System.Text.Json;
using Excalibur.Core.Automation;
using Excalibur.Core.Battery;
using Excalibur.Core.Casper;
using Excalibur.Core.History;
using Excalibur.Core.Lighting;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.Core.Fan.Ipc;

/// <summary>
/// The fan service's commands (<see cref="FanRequest"/> in, <see cref="FanResponse"/> out), shared by the Windows service's
/// named pipe and the Linux service's Unix socket, so both speak the same language to their windows. Every command is
/// validated here and again by <see cref="FanControlLoop"/>, which also keeps the safety policy above it.
/// </summary>
/// <param name="casper">Casper Control Center clean-up; null where there is none (Linux).</param>
/// <param name="shutdown">Runs the "shutdown" command (stop the service); null where it is not offered.</param>
/// <param name="warn">Problems worth a log line that do not fail the command.</param>
public sealed class FanCommandHandler(
    FanControlLoop loop,
    bool simulated,
    DateTimeOffset startedAt,
    ServiceSettingsStore settings,
    HistoryRecorder history,
    PowerManager power,
    LightingManager lighting,
    BatteryMonitor battery,
    BatteryRecorder batteryRecorder,
    BatteryEstimates batteryEstimates,
    ProfileSwitcher switcher,
    FanFileLog fileLog,
    CasperCleaner? casper = null,
    Action? shutdown = null,
    Action<string>? warn = null)
{
    public const int MaxLineLength = 4096;

    // The program whose rule is in force (setAppOverride), so the tray can tell after a service restart that it must send it again.
    private volatile string? _appProgram;

    /// <summary>One request line in, one response out; never throws for a bad request.</summary>
    public FanResponse Handle(string line)
    {
        if (line.Length > MaxLineLength) return new FanResponse(false, "request too long");
        FanRequest? req;
        try { req = JsonSerializer.Deserialize<FanRequest>(line, FanPipe.Json); }
        catch (JsonException) { return new FanResponse(false, "invalid JSON"); }
        // A request without "cmd" deserialises with Cmd null despite its type, and dropped the connection.
        if (req?.Cmd is null) return new FanResponse(false, "empty request");
        return Handle(req);
    }

    public FanResponse Handle(FanRequest req)
    {
        var now = DateTimeOffset.Now;
        try
        {
            switch (req.Cmd.ToLowerInvariant())
            {
                case "status":
                    break;
                case "events":
                    return new FanResponse(true, Status: Status(), Events: loop.Events.Select(e => new FanEventDto(e.At, e.Message)).ToList());
                case "setprofile":
                    if (req.Profile is null || settings.Current.FindProfile(req.Profile) is not { } profile)
                        return new FanResponse(false, $"unknown profile '{req.Profile}'");
                    switcher.Switch(profile, now, req.Source);
                    break;
                case "release":
                    switcher.Switch(FanProfile.EcAuto, now, req.Source);
                    break;
                case "setmanual":
                    if (req.Cpu is not { } cpu || req.Gpu is not { } gpu || req.Minutes is not { } minutes)
                        return new FanResponse(false, "setManual needs cpu, gpu and minutes");
                    loop.SetManual(cpu, gpu, TimeSpan.FromMinutes(minutes), now, req.Source);
                    break;
                case "history":
                    var to = req.To ?? now;
                    var from = req.From ?? to.AddMinutes(-10);
                    if (from >= to || to - from > TimeSpan.FromDays(8))
                        return new FanResponse(false, "history needs from < to, at most 8 days apart");
                    int points = Math.Clamp(req.MaxPoints ?? 600, 1, 4000);
                    return new FanResponse(true, Status: Status(), History: history.Query(from.UtcDateTime, to.UtcDateTime, points));
                case "setpowermode":
                    if (req.PowerMode is not { } mode || !Enum.IsDefined(mode))
                        return new FanResponse(false, "setPowerMode needs a powerMode");
                    var result = power.SetMode(mode);
                    return result.Ok
                        ? new FanResponse(true, Status: Status())
                        : new FanResponse(false, result.Message, Status: Status(), Code: result.Code);
                case "setcpulimit":
                    // No cpuLimit = the power mode decides again.
                    var limitResult = power.SetCpuLimit(req.CpuLimit);
                    return limitResult.Ok
                        ? new FanResponse(true, Status: Status())
                        : new FanResponse(false, limitResult.Message, Status: Status(), Code: limitResult.Code);
                case "setgpulimit":
                    // No gpuLimitWatts = the power mode decides again.
                    var gpuResult = power.SetGpuLimit(req.GpuLimitWatts);
                    return gpuResult.Ok
                        ? new FanResponse(true, Status: Status())
                        : new FanResponse(false, gpuResult.Message, Status: Status(), Code: gpuResult.Code);
                case "getsettings":
                    return new FanResponse(true, Status: Status(), Settings: settings.Current);
                case "updatesettings":
                    if (req.Settings is null) return new FanResponse(false, "updateSettings needs settings");
                    var errors = settings.Update(req.Settings.ApplyTo, out bool saved);
                    if (errors.Count > 0) return new FanResponse(false, string.Join("; ", errors));
                    if (!saved) warn?.Invoke($"settings applied but could not be saved to {settings.Path}");
                    return new FanResponse(true, Status: Status(), Settings: settings.Current);
                case "listprofiles":
                    return ProfileList();
                case "saveprofile":
                    return SaveProfile(req.Definition, now);
                case "deleteprofile":
                    return DeleteProfile(req.Profile, now);
                case "setslots":
                    return SetSlots(req.Names);
                case "shutdown" when shutdown is not null:
                    fileLog.Write(now, "shutdown requested over the pipe");
                    shutdown();
                    return new FanResponse(true, Status: Status());
                case "trialec":
                    try { loop.StartEcTrial(now); }
                    catch (InvalidOperationException ex) { return new FanResponse(false, ex.Message, Status: Status(), Code: "trial_unavailable"); }
                    break;
                case "setlighting":
                    if (req.Lighting is null) return new FanResponse(false, "setLighting needs lighting");
                    var lightErrors = lighting.Set(req.Lighting);
                    if (lightErrors.Count > 0) return new FanResponse(false, string.Join("; ", lightErrors));
                    return new FanResponse(true, Status: Status(), Settings: settings.Current);
                case "setlightidle":
                    if (req.Idle is not { } idle) return new FanResponse(false, "setLightIdle needs idle");
                    lighting.SetIdle(idle);
                    break;
                case "setappoverride":
                    SetAppOverride(req.AppOverride, now);
                    break;
                case "clearfault":
                    loop.ClearFault(now);
                    break;
                case "casperscan" when casper is not null:
                    return new FanResponse(true, Status: Status(), Casper: casper.Scan());
                case "casperdisable" when casper is not null:
                {
                    var r = casper.Disable();
                    fileLog.Write(now, $"casper: turned off ({r.Items.Count(i => i.Kind != CasperKind.Warning)} parts){(r.Error is null ? "" : $", errors: {r.Error}")}");
                    return new FanResponse(true, Status: Status(), Casper: r);
                }
                case "casperrestore" when casper is not null:
                {
                    var r = casper.Restore();
                    fileLog.Write(now, $"casper: restored{(r.Error is null ? "" : $", errors: {r.Error}")}");
                    return new FanResponse(true, Status: Status(), Casper: r);
                }
                default:
                    return new FanResponse(false, $"unknown command '{req.Cmd}'");
            }
        }
        catch (ArgumentException ex) { return new FanResponse(false, ex.Message); }
        return new FanResponse(true, Status: Status());
    }

    private FanResponse ProfileList()
    {
        var s = settings.Current;
        var profiles = new List<ProfileInfoDto>();
        foreach (var p in FanProfile.BuiltIn)
            profiles.Add(p.IsEcAuto ? new(p.Name, true, null, null) : new(p.Name, true, p.CpuCurve.Points, p.GpuCurve.Points));
        profiles.AddRange(s.CustomProfiles.Select(c => new ProfileInfoDto(c.Name, false, c.Cpu, c.Gpu ?? c.Cpu)));
        return new FanResponse(true, Status: Status(), Profiles: profiles, Slots: s.EffectiveSlots);
    }

    private FanResponse SaveProfile(CustomProfile? definition, DateTimeOffset now)
    {
        if (definition is null) return new FanResponse(false, "saveProfile needs a definition", Code: "profile_invalid");
        var problems = definition.Validate();
        if (problems.Count > 0) return new FanResponse(false, string.Join("; ", problems), Code: "profile_invalid");
        var errors = settings.Update(s => ProfileLibrary.Save(s, definition), out bool saved);
        if (errors.Count > 0) return new FanResponse(false, string.Join("; ", errors), Code: "profile_invalid");
        if (!saved) warn?.Invoke($"profile applied but could not be saved to {settings.Path}");
        string name = definition.Name.Trim();
        fileLog.Write(now, $"custom profile saved: {name}");
        // Editing the profile that is running takes effect at once.
        if (string.Equals(loop.Status.Profile, name, StringComparison.OrdinalIgnoreCase) && settings.Current.FindProfile(name) is { } edited)
            loop.SetProfile(edited, now, FanSource.Service);
        return ProfileList();
    }

    private FanResponse DeleteProfile(string? name, DateTimeOffset now)
    {
        if (name is null) return new FanResponse(false, "deleteProfile needs a profile", Code: "profile_not_found");
        if (FanProfile.FindBuiltIn(name) is not null) return new FanResponse(false, $"'{name}' is built in and cannot be deleted", Code: "profile_builtin");
        if (!settings.Current.CustomProfiles.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            return new FanResponse(false, $"no custom profile '{name}'", Code: "profile_not_found");
        // If it is running, move to Balanced first so the fans never sit on a profile that no longer exists.
        if (string.Equals(loop.Status.Profile, name, StringComparison.OrdinalIgnoreCase))
            switcher.Switch(FanProfile.Balanced, now, FanSource.Service);
        var errors = settings.Update(s => ProfileLibrary.Delete(s, name), out _);
        if (errors.Count > 0) return new FanResponse(false, string.Join("; ", errors), Code: "profile_invalid");
        fileLog.Write(now, $"custom profile deleted: {name}");
        return ProfileList();
    }

    private FanResponse SetSlots(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count == 0) return new FanResponse(false, "setSlots needs 1-4 profile names", Code: "slots_invalid");
        var errors = settings.Update(s => ProfileLibrary.SetSlots(s, names), out _);
        return errors.Count > 0 ? new FanResponse(false, string.Join("; ", errors), Code: "slots_invalid") : ProfileList();
    }

    private void SetAppOverride(AppOverride? rule, DateTimeOffset now)
    {
        if (rule?.Validate() is { Count: > 0 } errors) throw new ArgumentException(string.Join("; ", errors));
        if (rule is null && _appProgram is null) return;
        _appProgram = rule?.Program;
        fileLog.Write(now, rule is null ? "app rule ended"
            : $"app rule: {rule.Program} (profile {rule.Profile ?? "-"}, mode {rule.PowerMode?.ToString() ?? "-"}, CPU limit "
              + (rule.CpuLimit is null ? "-" : rule.CpuLimit.Limit is { } l ? $"{l.Pl1Watts}/{l.Pl2Watts} W" : "none") + ")");
        switcher.SetOverride(rule?.Profile, now);
        power.SetAppOverride(rule?.PowerMode, rule?.CpuLimit);
    }

    public FanStatusDto Status() =>
        FanStatusDto.From(loop.Status, simulated, startedAt) with
        {
            Power = PowerStatusDto.From(power.Status),
            LightIdleOffMinutes = settings.Current.Lighting?.IdleOffMinutes ?? 0,
            LightIdle = lighting.Idle,
            // The light key changes it behind the page's back; the page follows this.
            LightBrightness = settings.Current.Lighting?.Brightness,
            AppProgram = _appProgram,
            Battery = BatteryStatusDto.From(battery.Live(), battery.Health, batteryRecorder.Last, batteryEstimates.All, batteryEstimates.Idle),
        };
}
