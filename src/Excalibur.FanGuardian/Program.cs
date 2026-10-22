using System.Security.Principal;
using Excalibur.Core.Fan;
using Excalibur.Core.Hardware;
using Excalibur.FanGuardian;

// Excalibur fan guardian. Runs as a Windows service ("ExcaliburFanGuardian"), or from an
// elevated console for testing. It only ever writes 0xFF (EC automatic) to the fan targets:
// see GuardianMonitor for when.

using (var id = WindowsIdentity.GetCurrent())
{
    if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
    {
        Console.Error.WriteLine("The guardian needs administrator rights (PawnIO).");
        return 2;
    }
}

using var single = new Mutex(false, @"Global\ExcaliburFanGuardian", out _);
if (!single.WaitOne(0))
{
    Console.Error.WriteLine("Another guardian instance is already running.");
    return 3;
}

string module = Path.Combine(AppContext.BaseDirectory, "pawnio-modules", "LpcACPIEC.bin");
using var ec = EmbeddedController.Open(module);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "ExcaliburFanGuardian");
// The guardian only needs the targets now and then; fewer EC reads, fewer collisions with Windows.
// Shorter than the service's minute: it must notice a target the dead service left behind soon.
builder.Services.AddSingleton<IFanHardware>(new EcFanHardware(ec, new GmWmiGateway(), TimeSpan.FromSeconds(10)));
var fileLog = new FanFileLog(FanPaths.LogDirectory, "guardian");
SlowOps.Report = m => fileLog.Write(DateTimeOffset.Now, m);
builder.Services.AddSingleton(fileLog);
builder.Services.AddHostedService<GuardianWorker>();

using var host = builder.Build();
host.Run();
return 0;
