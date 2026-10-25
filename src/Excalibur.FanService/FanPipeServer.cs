using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Excalibur.Core.Battery;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.History;
using Excalibur.Core.Lighting;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.FanService;

/// <summary>
/// Serves <see cref="FanPipe.Name"/>: one JSON line in, one JSON line out, any number of
/// exchanges per connection. Local interactive users may connect (the UI does not need
/// administrator rights); network logons are denied. Every command is validated here and
/// again by <see cref="FanControlLoop"/>, which also keeps the safety policy above it.
/// </summary>
public sealed class FanPipeServer(
    FanControlLoop loop,
    FanRuntime runtime,
    ServiceSettingsStore settings,
    HistoryRecorder history,
    PowerManager power,
    LightingManager lighting,
    BatteryMonitor battery,
    BatteryRecorder batteryRecorder,
    BatteryEstimates batteryEstimates,
    ProfileSwitcher switcher,
    IHostApplicationLifetime lifetime,
    FanFileLog fileLog,
    ILogger<FanPipeServer> logger) : BackgroundService
{
    private const int MaxClients = 8;
    /// <summary>Instances waiting at once, so a client arriving while another is being accepted never finds the pipe busy.</summary>
    private const int Listeners = 3;
    private const int MaxLineLength = 4096;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Listeners).Select(_ => Task.Run(() => ListenAsync(stoppingToken), stoppingToken)));

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        // A pipe problem must never take the fan loop down with it, so errors here are
        // logged and retried instead of escaping (which would stop the whole host).
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = Create();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "could not create the control pipe; retrying in 5 s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { break; }
                continue;
            }
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                await pipe.DisposeAsync();
                if (ex is OperationCanceledException) break;
                logger.LogWarning(ex, "control pipe connection failed");
                continue;
            }
            _ = Task.Run(() => ServeAsync(pipe, stoppingToken), stoppingToken);
        }
    }

    private NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();
        // The account running the service (LocalSystem, or the user in --simulate) owns the pipe
        // and needs full control to create further instances.
        using (var me = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(runtime.Simulated ? FanPipe.SimName : FanPipe.Name, PipeDirection.InOut, MaxClients,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (line is null) break;
                    var response = line.Length > MaxLineLength
                        ? new FanResponse(false, "request too long")
                        : Handle(line);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, FanPipe.Json));
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            catch (Exception ex) { logger.LogWarning(ex, "pipe client failed"); }
        }
    }

    private FanResponse Handle(string line)
    {
        FanRequest? req;
        try { req = JsonSerializer.Deserialize<FanRequest>(line, FanPipe.Json); }
        catch (JsonException) { return new FanResponse(false, "invalid JSON"); }
        if (req is null) return new FanResponse(false, "empty request");

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
                    SwitchProfile(profile, now, req.Source);
                    break;
                case "release":
                    SwitchProfile(FanProfile.EcAuto, now, req.Source);
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
                case "getsettings":
                    return new FanResponse(true, Status: Status(), Settings: settings.Current);
                case "updatesettings":
                    if (req.Settings is null) return new FanResponse(false, "updateSettings needs settings");
                    var errors = settings.Update(req.Settings.ApplyTo, out bool saved);
                    if (errors.Count > 0) return new FanResponse(false, string.Join("; ", errors));
                    if (!saved) logger.LogWarning("settings applied but could not be saved to {Path}", settings.Path);
                    return new FanResponse(true, Status: Status(), Settings: settings.Current);
                case "listprofiles":
                    return ProfileList();
                case "saveprofile":
                    return SaveProfile(req.Definition, now);
                case "deleteprofile":
                    return DeleteProfile(req.Profile, now);
                case "setslots":
                    return SetSlots(req.Names);
                case "shutdown":
                    ScheduleShutdown();
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
                case "clearfault":
                    loop.ClearFault(now);
                    break;
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
        if (!saved) logger.LogWarning("profile applied but could not be saved to {Path}", settings.Path);
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
            SwitchProfile(FanProfile.Balanced, now, FanSource.Service);
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

    /// <summary>
    /// Stops the guardian first (it would start this service again), then this service, which hands the fans
    /// to the EC on the way out. Delayed a moment so the reply reaches the caller.
    /// </summary>
    private void ScheduleShutdown()
    {
        fileLog.Write(DateTimeOffset.Now, "shutdown requested over the pipe");
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            try
            {
                using var guardian = new System.ServiceProcess.ServiceController("ExcaliburFanGuardian");
                if (guardian.Status is System.ServiceProcess.ServiceControllerStatus.Running)
                {
                    guardian.Stop();
                    guardian.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "could not stop the guardian service"); }
            lifetime.StopApplication();
        });
    }

    private void SwitchProfile(FanProfile profile, DateTimeOffset now, string? source) => switcher.Switch(profile, now, source);

    private FanStatusDto Status() =>
        FanStatusDto.From(loop.Status, runtime.Simulated, runtime.StartedAt) with
        {
            Power = PowerStatusDto.From(power.Status),
            LightIdleOffMinutes = settings.Current.Lighting?.IdleOffMinutes ?? 0,
            LightIdle = lighting.Idle,
            // The light key changes it behind the page's back; the page follows this.
            LightBrightness = settings.Current.Lighting?.Brightness,
            Battery = BatteryStatusDto.From(battery.Live(), battery.Health, batteryRecorder.Last, batteryEstimates.All, batteryEstimates.Idle),
        };
}
