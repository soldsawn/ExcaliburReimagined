using System.Security.Principal;
using Excalibur.Core.Battery;
using Excalibur.Core.Fan;
using Excalibur.Core.Hardware;
using Excalibur.Core.History;
using Excalibur.Core.Lighting;
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
// --ec <version> pretends another EC firmware in the simulation (to try the unknown-EC trial).
if (fake is not null && Array.FindIndex(args, a => a.Equals("--ec", StringComparison.OrdinalIgnoreCase)) is int ecArg and >= 0 && ecArg + 1 < args.Length)
    fake.EcVersion = args[ecArg + 1];
EmbeddedController? ec = null;
PawnIoCpuPowerRegisters? cpuRegisters = null;
Timer? cpuSampler = null;
IFanHardware hardware;
IPowerHardware powerHardware;
IBatteryHardware batteryHardware;
IBatteryMeter batteryMeter;
ISmiChannel firmware;
if (fake is not null)
{
    hardware = fake;
    // --on-battery pretends the charger is unplugged (to see the battery page measure).
    bool simOnBattery = args.Contains("--on-battery", StringComparer.OrdinalIgnoreCase);
    powerHardware = new SimulatedPowerHardware { OnBattery = simOnBattery };
    var simBattery = new SimulatedBatteryHardware { OnBattery = simOnBattery };
    (batteryHardware, batteryMeter) = (simBattery, simBattery);
    firmware = new SimulatedSmiChannel();
}
else
{
    string module = Path.Combine(AppContext.BaseDirectory, "pawnio-modules", "LpcACPIEC.bin");
    ec = EmbeddedController.Open(module);
    // One gateway for fans, power and light: the firmware has a single response buffer.
    var gateway = new GmWmiGateway();
    firmware = gateway;
    // CPU power, limits and temperature; without the modules the status has no CPU watts, CPU limits cannot be set
    // and the CPU temperature comes from the slower WMI block.
    cpuRegisters = PawnIoCpuPowerRegisters.TryOpen(Path.Combine(AppContext.BaseDirectory, "pawnio-modules"));
    Func<int?>? cpuTemperature = null;
    if (cpuRegisters is not null)
    {
        // Sampled every 200 ms, used as the median of the last 2 s: single readings jump too much (TemperatureMedian).
        var msr = new MsrCpuTemperature(cpuRegisters);
        var median = new TemperatureMedian(TimeSpan.FromSeconds(2));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        cpuSampler = new Timer(_ => { if (msr.Read() is { } c) median.Add(c, clock.Elapsed); }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
        cpuTemperature = () => median.Value(clock.Elapsed);
    }
    hardware = new EcFanHardware(ec, gateway, cpuTemperature: cpuTemperature,
        versionCachePath: Path.Combine(runtimeDir, "ec-version.txt"), scale: new FanRpmScale(Path.Combine(runtimeDir, FanRpmScale.FileName)));
    var wmiBattery = new WmiBatteryHardware();
    (batteryHardware, batteryMeter) = (wmiBattery, wmiBattery);
    powerHardware = cpuRegisters is null
        ? new EcPowerHardware(gateway)
        : new EcPowerHardware(gateway, new CpuPowerMeter(cpuRegisters), new CpuLimiter(cpuRegisters));
}

var runtime = new FanRuntime(simulate, fake, DateTimeOffset.Now, runtimeDir);
builder.Services.AddSingleton(runtime);
var settingsStore = new ServiceSettingsStore(runtime.SettingsPath);
var loop = new FanControlLoop(hardware, new FanLoopOptions
{
    KnownEcVersions = new HashSet<string>(new FanLoopOptions().KnownEcVersions.Concat(settingsStore.Current.TrustedEcVersions)),
});
// A version that passed the user's trial stays trusted after a restart, with the profile the trial left running.
loop.EcTrusted += (v, profile) => settingsStore.Update(s => s with
{
    TrustedEcVersions = s.TrustedEcVersions.Contains(v) ? s.TrustedEcVersions : [.. s.TrustedEcVersions, v],
    Profile = profile.Name,
}, out _);
builder.Services.AddSingleton(loop);
var fileLog = new FanFileLog(runtime.LogDirectory, "fan");
SlowOps.Report = m => fileLog.Write(DateTimeOffset.Now, m);
if (!simulate)
{
    EcTrace.Report = m => fileLog.Write(DateTimeOffset.Now, m);
    EcQuiet.Report = m => fileLog.Write(DateTimeOffset.Now, m);
    EcTrace.WatchAcpi(m => fileLog.Write(DateTimeOffset.Now, m));
}
if (hardware is EcFanHardware ecFans) ecFans.Log = m => fileLog.Write(DateTimeOffset.Now, m);
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
        m => fileLog.Write(DateTimeOffset.Now, m), simulate ? TimeSpan.Zero : EcQuiet.AfterCharger);
});
builder.Services.AddSingleton(new BatteryMonitor(batteryHardware, m => fileLog.Write(DateTimeOffset.Now, m)));
builder.Services.AddSingleton(new BatteryRecorder(batteryMeter));
string batteryDir = Path.Combine(runtimeDir, "battery");
builder.Services.AddSingleton(new BatteryCsvLog(batteryDir, m => fileLog.Write(DateTimeOffset.Now, m)));
builder.Services.AddSingleton(BatteryEstimates.Load(batteryDir, m => fileLog.Write(DateTimeOffset.Now, m)));
builder.Services.AddSingleton(new LightingManager(new KeyboardLight(firmware), settingsStore, m => fileLog.Write(DateTimeOffset.Now, m)));
builder.Services.AddHostedService<FanWorker>();
builder.Services.AddHostedService<PowerWorker>();
builder.Services.AddHostedService<FanPipeServer>();
builder.Services.AddHostedService<LightingWorker>();
builder.Services.AddHostedService<BatteryWorker>();

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
    cpuSampler?.Dispose();
    cpuRegisters?.Dispose();
}
return Environment.ExitCode;

static bool IsElevated()
{
    using var id = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
}
