using System.ServiceProcess;

namespace Excalibur.App.Tray;

/// <summary>
/// The tray's "Exit" stops the fan service and its guardian (the <c>shutdown</c> command); opening the app again starts
/// them here. The setup lets the signed-in user start (only start and query) both services, so no administrator prompt.
/// </summary>
internal static class FanServices
{
    private static readonly string[] Names = ["ExcaliburFanGuardian", "ExcaliburFan"];

    /// <summary>Starts whichever of the two services is stopped; never throws (an older install may not allow it yet).</summary>
    public static void StartIfStopped()
    {
        foreach (string name in Names)
        {
            try
            {
                using var service = new ServiceController(name);
                if (service.Status != ServiceControllerStatus.Stopped) continue;
                service.Start();
                TrayLog.Write($"services: started {name}");
            }
            catch (InvalidOperationException ex)
            {
                // Not installed, already starting (the guardian starts the fan service too), or the install predates the
                // start permission: the tray then shows "cannot reach" as before.
                TrayLog.Write($"services: could not start {name} ({ex.InnerException?.Message ?? ex.Message})");
            }
        }
    }
}
