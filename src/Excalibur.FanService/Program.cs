using System.Security.Principal;
using Excalibur.Core.Fan;
using Excalibur.Core.Hardware;
using Excalibur.Core.History;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;
using Excalibur.FanService;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32;

// Excalibur fan service. Runs as a Windows service ("ExcaliburFan"), or from an elevated
// console for testing (Ctrl+C stops it and hands the fans back to the EC).
//   --simulate   no EC access at all: an in-memory model instead (no admin needed),
//                for trying the pipe protocol and the UI safely.

bool simulate = args.Contains("--simulate", StringComparer.OrdinalIgnoreCase);
bool asService = WindowsServiceHelpers.IsWindowsService();

if (!simulate && !IsElevated())
{
    Console.Error.WriteLine("The fan service needs administrator rights (PawnIO). Use --simulate to try it without the EC.");
    return 2;
}

// One fan controller per machine: a console instance and the service must never both write.
using var single = new Mutex(false, simulate ? @"Global\ExcaliburFanServiceSim" : @"Global\ExcaliburFanService", out _);
if (!single.WaitOne(0))
{
    Console.Error.WriteLine("Another Excalibur fan service instance is already running.");
    return 3;
}

var runtimeDir = simulate ? Path.Combine(FanPaths.DataDirectory, "sim") : FanPaths.DataDirectory;
Directory.CreateDirectory(runtimeDir);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "ExcaliburFan");
builder.Services.AddSingleton<PowerSignal>();
if (asService) builder.Services.AddSingleton<IHostLifetime, FanServiceLifetime>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));

FakeFanHardware? fake = simulate ? new FakeFanHardware() : null;
EmbeddedController? ec = null;
IFanHardware hardware;
IPowerHardware powerHardware;
if (fake is not null)
{
    hardware = fake;
    powerHardware = new SimulatedPowerHardware();
}
else
{
    string module = Path.Combine(AppContext.BaseDirectory, "pawnio-modules", "LpcACPIEC.bin");
    ec = EmbeddedController.Open(module);
    // One gateway for fans and power: the firmware has a single response buffer.
    var gateway = new GmWmiGateway();
    hardware = new EcFanHardware(ec, gateway);
    powerHardware = new EcPowerHardware(gateway);
}

var runtime = new FanRuntime(simulate, fake, DateTimeOffset.Now, runtimeDir);
builder.Services.AddSingleton(runtime);
builder.Services.AddSingleton(new FanControlLoop(hardware));
var settingsStore = new ServiceSettingsStore(runtime.SettingsPath);
var fileLog = new FanFileLog(runtime.LogDirectory, "fan");
var history = new HistoryRecorder(runtime.HistoryDirectory, settingsStore.Current.History, m => fileLog.Write(DateTimeOffset.Now, m));
settingsStore.Changed += s => history.Mode = s.History;
builder.Services.AddSingleton(settingsStore);
builder.Services.AddSingleton(fileLog);
builder.Services.AddSingleton(history);
builder.Services.AddSingleton(powerHardware);
builder.Services.AddSingleton<ProfileSwitcher>();
builder.Services.AddSingleton(sp =>
{
    var switcher = sp.GetRequiredService<ProfileSwitcher>();
    return new PowerManager(powerHardware, settingsStore, (profile, source) => switcher.Switch(profile, source),
        m => fileLog.Write(DateTimeOffset.Now, m));
});
builder.Services.AddHostedService<FanWorker>();
builder.Services.AddHostedService<PowerWorker>();
builder.Services.AddHostedService<FanPipeServer>();

using var host = builder.Build();

if (!asService)
{
    // Console mode gets suspend/resume from SystemEvents (a service gets them from the SCM instead).
    var power = host.Services.GetRequiredService<PowerSignal>();
    SystemEvents.PowerModeChanged += (_, e) =>
    {
        if (e.Mode == PowerModes.Suspend) power.Raise(suspending: true);
        else if (e.Mode == PowerModes.Resume) power.Raise(suspending: false);
    };
}

try
{
    host.Run();
}
finally
{
    history.Dispose();
    ec?.Dispose();
}
return Environment.ExitCode;

static bool IsElevated()
{
    using var id = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
}
