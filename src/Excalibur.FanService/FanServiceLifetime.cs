using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace Excalibur.FanService;

/// <summary>
/// The Windows service lifetime, plus power events: a service in session 0 does not get
/// SystemEvents, so suspend/resume arrive here from the service control manager.
/// </summary>
public sealed class FanServiceLifetime : WindowsServiceLifetime
{
    private readonly PowerSignal _power;

    public FanServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        PowerSignal power)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _power = power;
        CanHandlePowerEvent = true;
        CanShutdown = true;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus == PowerBroadcastStatus.Suspend) _power.Raise(suspending: true);
        else if (powerStatus is PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeAutomatic) _power.Raise(suspending: false);
        return base.OnPowerEvent(powerStatus);
    }
}
