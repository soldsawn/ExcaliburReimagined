using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Excalibur.Core.Automation;
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
/// administrator rights); network logons are denied. The commands themselves are
/// <see cref="FanCommandHandler"/>'s, shared with the Linux service.
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
    Excalibur.Core.Casper.CasperCleaner casper,
    ILogger<FanPipeServer> logger) : BackgroundService
{
    private const int MaxClients = 8;
    /// <summary>Instances waiting at once, so a client arriving while another is being accepted never finds the pipe busy.</summary>
    private const int Listeners = 3;
    private FanCommandHandler? _handler;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _handler = new FanCommandHandler(loop, runtime.Simulated, runtime.StartedAt, settings, history, power, lighting, battery,
            batteryRecorder, batteryEstimates, switcher, fileLog, casper, ScheduleShutdown, m => logger.LogWarning("{Message}", m));
        return Task.WhenAll(Enumerable.Range(0, Listeners).Select(_ => Task.Run(() => ListenAsync(stoppingToken), stoppingToken)));
    }

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
                    var response = _handler!.Handle(line);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, FanPipe.Json));
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            catch (Exception ex) { logger.LogWarning(ex, "pipe client failed"); }
        }
    }

    /// <summary>
    /// Stops the guardian first (it would start this service again), then this service, which hands the fans
    /// to the EC on the way out. Delayed a moment so the reply reaches the caller.
    /// </summary>
    private void ScheduleShutdown()
    {
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
}
