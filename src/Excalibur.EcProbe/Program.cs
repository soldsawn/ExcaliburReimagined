using System.Security.Principal;
using System.Text;
using Excalibur.Core.Hardware;

// EC discovery probe (READ-ONLY). Modes:
//
//   verify            Reads only CPU/GPU temperature registers and checks them
//                     against the firmware's own WMI readings. Gentle first-contact
//                     test that proves the EC read path is correct. Touches 2 regs.
//
//   watch             Polls the candidate fan/thermal registers (see Candidates)
//                     ~every 750 ms and prints a row whenever any of them changes,
//                     with the WMI temps and fan RPM alongside for correlation.
//                     This is the discovery tool: start it, then trigger Fan Max
//                     (Fn hotkey) and a CPU/GPU load and watch which EC offset
//                     tracks the fan. Runs until Ctrl+C. Saved to analysis/ec.
//
//   full              Best-effort read of the whole 0x00-0xFF window (slow; many
//                     reserved offsets will time out even with recovery).
//
//   <label>           Focused read-only dump of just the candidate registers,
//                     annotated and saved to analysis/ec/<label>-<timestamp>.txt
//                     e.g. "idle", "load", "fanmax". Reliable (skips reserved regs).
//
//   fanmax-test       The only writing modes: fanmax-test (FANM 0xC3), fanpct-test and
//   fanpct-test       fanslow-test and gpuslow-test (fan target 0x2C CPU / 0x2D GPU).
//   fanslow-test      Each asks for EVET and restores the value.
//   gpuslow-test      fanfloor-test / gpufloor-test step the target down to find the EC's floor.
//
//   msr               CPU power limits PL1/PL2 and present package power (IntelMSR module,
//                     never touches the EC). Saved to analysis/ec/msr-<timestamp>.txt.
//   cputemp [s]       CPU package temperature from MSR 0x1A2/0x1B1 every 250 ms for s seconds (default 120),
//                     READ-ONLY, never the EC or WMI (safe beside the running service). Saved to analysis/ec.
//   led-test          Keyboard light (WMI SET 0x0100, never the EC): steps through colours, zones and
//                     effects every 4 s so you can note what the keyboard shows, then puts back what
//                     GET 0x0100 reported at the start. Asks for EVET. Saved to analysis/ec.
//   msr-test          Writes lower CPU limits (PL1 15 W, PL2 20 W) to MSR 0x610 for 40 s while
//                     printing the package power, then restores the original. Asks for EVET.
//
// Every other mode is read-only.

// Candidate registers to track during discovery. Contiguous-ish, known-populated
// offsets from docs/05 (the ACPI DSDT map) — temps, fan RPM, perf mode, Fn code,
// and the suspected fan-control fields. We deliberately avoid the reserved regions
// that desync the controller.
(byte Addr, string Name)[] candidates =
[
    (0x29, "NPL1"), (0x2A, "NPL2"), (0x32, "BCLC"), (0x48, "LKCL"),
    (0x58, "RTMP"), (0x95, "APMD"), (0xA2, "CBSC"),
    (0xB0, "RPM1"), (0xB1, "RPM2"), (0xB2, "RPM3"), (0xB3, "RPM4"),
    (0xB7, "VGAT"), (0xB8, "EPWM"),
    (0xBE, "HSRS"), (0xBF, "HSRE"), (0xC0, "DDSS"), (0xC3, "FANM"), (0xD6, "FAND"),
];

string mode = args.Length > 0 ? args[0] : "verify";
string modulePath = args.Length > 1
    ? args[1]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                   "tools", "pawnio-modules", "LpcACPIEC.bin");

if (!IsElevated())
{
    Console.Error.WriteLine("This probe needs an elevated (Run as administrator) shell to reach PawnIO.");
    return 1;
}
if (mode.Equals("led-test", StringComparison.OrdinalIgnoreCase))
    return LedTest();
// Before the EC module check and the WMI gateway: this mode touches neither.
if (mode.Equals("cputemp", StringComparison.OrdinalIgnoreCase))
    return CpuTemp(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "pawnio-modules", "IntelMSR.bin"),
        args.Length > 1 && int.TryParse(args[1], out int secs) ? secs : 120);

modulePath = Path.GetFullPath(modulePath);
if (!File.Exists(modulePath))
{
    Console.Error.WriteLine($"LpcACPIEC module not found at: {modulePath}");
    return 2;
}

var wmi = new GmWmiGateway();
bool haveWmi = wmi.IsAvailable();

if (mode.Equals("verify", StringComparison.OrdinalIgnoreCase))
    return Verify(modulePath, haveWmi, Sensors());

if (mode.Equals("watch", StringComparison.OrdinalIgnoreCase))
    return Watch(modulePath, haveWmi, candidates);

if (mode.Equals("diag", StringComparison.OrdinalIgnoreCase))
    return Diag(modulePath);

if (mode.Equals("scan", StringComparison.OrdinalIgnoreCase))
    return Scan(modulePath);

if (mode.Equals("fanmax-test", StringComparison.OrdinalIgnoreCase))
    return FanMaxTest(modulePath, haveWmi);

if (mode.Equals("fanpct-test", StringComparison.OrdinalIgnoreCase))
    return FanPctTest(modulePath, haveWmi, slow: false);

if (mode.Equals("fanslow-test", StringComparison.OrdinalIgnoreCase))
    return FanPctTest(modulePath, haveWmi, slow: true);

if (mode.Equals("gpuslow-test", StringComparison.OrdinalIgnoreCase))
    return FanPctTest(modulePath, haveWmi, slow: true, gpu: true);

if (mode.Equals("curvelog", StringComparison.OrdinalIgnoreCase))
    return CurveLog(modulePath, haveWmi);

if (mode.Equals("persist-test", StringComparison.OrdinalIgnoreCase))
    return PersistTest(modulePath, haveWmi);

if (mode.Equals("fanstate", StringComparison.OrdinalIgnoreCase))
    return FanState(modulePath, haveWmi);

if (mode.Equals("fanreset", StringComparison.OrdinalIgnoreCase))
    return FanReset(modulePath, haveWmi);

if (mode.Equals("fanfloor-test", StringComparison.OrdinalIgnoreCase))
    return FanFloorTest(modulePath, haveWmi, gpu: false);

if (mode.Equals("gpufloor-test", StringComparison.OrdinalIgnoreCase))
    return FanFloorTest(modulePath, haveWmi, gpu: true);

if (mode.Equals("msr", StringComparison.OrdinalIgnoreCase))
    return Msr(Path.Combine(Path.GetDirectoryName(modulePath)!, "IntelMSR.bin"));

if (mode.Equals("msr-test", StringComparison.OrdinalIgnoreCase))
    return MsrTest(Path.Combine(Path.GetDirectoryName(modulePath)!, "IntelMSR.bin"));

return FullDump(mode, modulePath, haveWmi, Sensors(), candidates);

// Reads the firmware's own sensor block (GET 0x0200): side-effect-free, so it is
// safe to poll. Returns default when WMI is unavailable.
SmiCommand Sensors() => haveWmi ? wmi.Execute(SmiCommand.Get(0x0200)) : default;

// --- verify: read only the two temperature registers and cross-check WMI ---
static int Verify(string modulePath, bool haveWmi, SmiCommand sensors)
{
    byte cpu, gpu;
    using (var ec = EmbeddedController.Open(modulePath))
    {
        cpu = ec.ReadByte(0x58); // RTMP
        gpu = ec.ReadByte(0xB7); // VGAT
    }

    var lines = new List<string> { $"EC read OK.  EC RTMP(0x58)={cpu}C  EC VGAT(0xB7)={gpu}C" };
    if (!haveWmi)
    {
        lines.Add("WMI not available to cross-check, but the EC read itself succeeded.");
    }
    else
    {
        lines.Add($"WMI reports: CPU={sensors.A2}C  GPU={sensors.A3}C");
        bool cpuOk = Math.Abs((int)cpu - (int)sensors.A2) <= 3;
        bool gpuOk = Math.Abs((int)gpu - (int)sensors.A3) <= 3;
        lines.Add(cpuOk && gpuOk
            ? "PASS: EC values match the firmware. The read path is correct."
            : "NOTE: values differ by more than 3C; the register map may differ on this unit.");
    }

    foreach (var l in lines) Console.WriteLine(l);
    Save($"verify-{Stamp()}.txt", string.Join(Environment.NewLine, lines));
    return 0;
}

// --- fanmax-test: the first EC WRITE. Sets FANM (0xC3) from 0 to 1, which is the same
//     bit Fn+1 flips, watches the fan for ~10 s, then restores the original value.
//     Only 0xC3 may ever be written here, only in the fan-increasing direction, only
//     after the user types EVET, and the restore runs even on Ctrl+C or an error. ---
static int FanMaxTest(string modulePath, bool haveWmi)
{
    const byte Fanm = 0xC3;
    var wmi = new GmWmiGateway();
    var log = new StringBuilder();
    void Say(string s) { Console.WriteLine(s); log.AppendLine(s); }

    using var ec = EmbeddedController.Open(modulePath);

    byte original = ec.ReadStable(Fanm);
    Say($"FANM (0x{Fanm:X2}) currently = 0x{original:X2}");
    if (original != 0)
    {
        Say("Turbo is already ON. Turn it off with Fn+1 first, then run this test again.");
        return 3;
    }

    Console.WriteLine();
    Console.WriteLine("This will WRITE the EC: FANM 0x00 -> 0x01 (fans to maximum) for ~10 seconds,");
    Console.WriteLine("then write 0x00 back. It cannot slow the fans. If the PC misbehaves, hold the");
    Console.WriteLine("power button 15-30 s to reset the EC.");
    Console.Write("Type EVET to continue: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "EVET", StringComparison.Ordinal))
    {
        Console.WriteLine("Cancelled. Nothing was written.");
        return 0;
    }

    bool cancelled = false;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled = true; };

    try
    {
        ec.WriteByte(Fanm, 0x01);
        Say($"{DateTime.Now:HH:mm:ss}  wrote 0x01 -> FANM, readback = 0x{ec.ReadStable(Fanm):X2}");

        for (int i = 0; i < 10 && !cancelled; i++)
        {
            Thread.Sleep(1000);
            var s = haveWmi ? wmi.Execute(SmiCommand.Get(0x0200)) : default;
            Say($"{DateTime.Now:HH:mm:ss}  FANM=0x{ec.ReadStable(Fanm):X2}  cpuRPM={s.A4} gpuRPM={s.A5}  cpu={s.A2}C gpu={s.A3}C");
        }
    }
    finally
    {
        // Always put the register back, even after Ctrl+C or an exception.
        try
        {
            ec.WriteByte(Fanm, original);
            Say($"{DateTime.Now:HH:mm:ss}  restored FANM to 0x{original:X2}, readback = 0x{ec.ReadStable(Fanm):X2}");
        }
        catch (Exception ex)
        {
            Say($"!! RESTORE FAILED ({ex.Message}). Press Fn+1 to toggle turbo by hand, or reboot.");
        }
    }

    for (int i = 0; i < 6; i++)
    {
        Thread.Sleep(2000);
        var s = haveWmi ? wmi.Execute(SmiCommand.Get(0x0200)) : default;
        Say($"{DateTime.Now:HH:mm:ss}  after restore: FANM=0x{ec.ReadStable(Fanm):X2}  cpuRPM={s.A4} gpuRPM={s.A5}");
    }

    string file = $"fanmax-test-{Stamp()}.txt";
    Save(file, log.ToString());
    Console.WriteLine($"Saved: {Path.Combine(OutDir(), file)}");
    return 0;
}

// --- fanpct-test / fanslow-test / gpuslow-test: EC WRITES to the per-fan target percent.
//     The EC firmware (decompiled from the CQ171 BIOS, see docs/06 phases 3.6c-3.7) treats EC
//     offset 0x2C as the CPU fan target in percent and 0x2D as the GPU one, 0xFF meaning "EC
//     automatic"; 0x2E/0x2F report the percent it actually applies. Each mode writes ONE
//     register for ~15 s, watches the fan, then writes 0xFF back. Only 0x2C or 0x2D may be
//     written, only from the 0xFF state, only after the user types EVET, with temperature
//     gates, and the restore runs even on Ctrl+C or an error. The slow modes also abort on
//     a temperature rise. ---
static int FanPctTest(string modulePath, bool haveWmi, bool slow, bool gpu = false)
{
    byte targetReg = gpu ? (byte)0x2D : (byte)0x2C, outputReg = gpu ? (byte)0x2F : (byte)0x2E;
    string name = gpu ? "GPU" : "CPU";
    const byte Fanm = 0xC3, Auto = 0xFF;
    // slow = the fan-REDUCING direction: stricter thermal gates, and it aborts on a temperature rise.
    byte target = slow ? (byte)45 : (byte)90;
    int startMaxC = slow ? 60 : 85, abortC = slow ? 70 : 90;
    const int SlowMaxRiseC = 2, OtherAbortC = 85;
    var wmi = new GmWmiGateway();
    var log = new StringBuilder();
    void Say(string s) { Console.WriteLine(s); log.AppendLine(s); }

    using var ec = EmbeddedController.Open(modulePath);

    // The watched temperature is the fan's own chip. WMI is preferred for the GPU because the EC's
    // VGAT read is easily corrupted by Windows' own EC driver sharing the ports.
    int WatchedTemp(SmiCommand s) => gpu
        ? (haveWmi ? (int)s.A3 : ec.ReadStable(0xB7))
        : ec.ReadStable(0x58);
    int OtherTemp(SmiCommand s) => gpu
        ? ec.ReadStable(0x58)
        : (haveWmi ? (int)s.A3 : ec.ReadStable(0xB7));

    byte original = ec.ReadStable(targetReg);
    byte output = ec.ReadStable(outputReg);
    byte fanm = ec.ReadStable(Fanm);
    var s0 = haveWmi ? wmi.Execute(SmiCommand.Get(0x0200)) : default;
    int temp = WatchedTemp(s0), other = OtherTemp(s0);
    string reg = $"0x{targetReg:X2}";
    Say($"{name} fan target ({reg}) = 0x{original:X2}, EC output (0x{outputReg:X2}) = {output}%, FANM = 0x{fanm:X2}, {name} = {temp}C, other chip = {other}C");
    if (original != Auto) { Say($"{reg} is not 0xFF (EC automatic). Refusing to write."); return 3; }
    if ((fanm & 1) != 0) { Say("Cooling Boost is ON. Turn it off with Fn+1 first."); return 3; }
    if (temp >= startMaxC) { Say($"{name} is at {temp}C (limit {startMaxC}C). Let it cool down first."); return 3; }
    if (other >= OtherAbortC) { Say($"The other chip is at {other}C (limit {OtherAbortC}C). Let it cool down first."); return 3; }
    if (!slow && output >= target) { Say($"EC output {output}% is already >= {target}%; this would not raise the fan. Refusing."); return 3; }
    if (slow && output <= target) { Say($"EC output {output}% is already <= {target}%; this would not slow the fan. Refusing."); return 3; }

    Console.WriteLine();
    Console.WriteLine($"This will WRITE the EC: {reg} 0xFF -> {target} ({name} fan target {target}%) for ~15 seconds,");
    Console.WriteLine("then write 0xFF (automatic) back. " + (slow
        ? $"This SLOWS the {name} fan below what the EC wants; it restores itself if the {name} reaches {abortC}C or rises more than {SlowMaxRiseC}C from {temp}C. "
        : $"It can only raise the fan above what the EC is doing now. The test restores itself if the {name} reaches {abortC}C. ") + "If the PC");
    Console.WriteLine("misbehaves, hold the power button 15-30 s to reset the EC.");
    Console.Write("Type EVET to continue: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "EVET", StringComparison.Ordinal))
    {
        Console.WriteLine("Cancelled. Nothing was written.");
        return 0;
    }

    bool cancelled = false;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled = true; };

    try
    {
        ec.WriteByte(targetReg, target);
        Say($"{DateTime.Now:HH:mm:ss}  wrote {target} -> {reg}, readback = 0x{ec.ReadStable(targetReg):X2}");

        for (int i = 0; i < 15 && !cancelled; i++)
        {
            Thread.Sleep(1000);
            var s = haveWmi ? wmi.Execute(SmiCommand.Get(0x0200)) : default;
            int t = WatchedTemp(s), o = OtherTemp(s);
            Say($"{DateTime.Now:HH:mm:ss}  {reg}=0x{ec.ReadStable(targetReg):X2}  out={ec.ReadStable(0x2E)}%/{ec.ReadStable(0x2F)}%  cpuRPM={s.A4} gpuRPM={s.A5}  {name}={t}C other={o}C");
            if (t >= abortC) { Say($"{name} reached {t}C; aborting and restoring."); break; }
            if (o >= OtherAbortC) { Say($"The other chip reached {o}C; aborting and restoring."); break; }
            if (slow && t > temp + SlowMaxRiseC) { Say($"{name} rose to {t}C (start {temp}C); aborting and restoring."); break; }
        }
    }
    finally
    {
        // Always put the register back, even after Ctrl+C or an exception.
        try
        {
            ec.WriteByte(targetReg, original);
            Say($"{DateTime.Now:HH:mm:ss}  restored {reg} to 0x{original:X2}, readback = 0x{ec.ReadStable(targetReg):X2}");
        }
        catch (Exception ex)
        {
            Say($"!! RESTORE FAILED ({ex.Message}). Reboot, or hold the power button 15-30 s to reset the EC.");
        }
    }

    for (int i = 0; i < 6; i++)
    {
        Thread.Sleep(2000);
        var s = haveWmi ? wmi.Execute(SmiCommand.Get(0x0200)) : default;
        Say($"{DateTime.Now:HH:mm:ss}  after restore: {reg}=0x{ec.ReadStable(targetReg):X2}  out={ec.ReadStable(outputReg)}%  cpuRPM={s.A4} gpuRPM={s.A5}");
    }

    string file = $"{(gpu ? "gpuslow" : slow ? "fanslow" : "fanpct")}-test-{Stamp()}.txt";
    Save(file, log.ToString());
    Console.WriteLine($"Saved: {Path.Combine(OutDir(), file)}");
    return 0;
}

// --- fanfloor-test / gpufloor-test: find the lowest fan percent the EC honours. Steps one fan's
//     target down 40, 35, 30, 25, 20 (never below 20), 8 s per step, and stops as soon as the RPM
//     stops following (the EC's own floor), the fan nearly stalls, or the chip warms up. Same gates
//     as the slow tests but tighter: start < 60C, abort at +2C from the start or 65C, the other
//     chip at 85C. Only 0x2C / 0x2D, only from 0xFF, only after EVET, restore always runs. ---
static int FanFloorTest(string modulePath, bool haveWmi, bool gpu)
{
    byte targetReg = gpu ? (byte)0x2D : (byte)0x2C, outputReg = gpu ? (byte)0x2F : (byte)0x2E;
    string name = gpu ? "GPU" : "CPU", reg = $"0x{targetReg:X2}";
    byte[] steps = [40, 35, 30, 25, 20];
    const byte Fanm = 0xC3, Auto = 0xFF;
    const int StartMaxC = 60, AbortC = 65, MaxRiseC = 2, OtherAbortC = 85, StallRpm = 800, FlatRpm = 100, DwellSeconds = 8;
    var wmi = new GmWmiGateway();
    var log = new StringBuilder();
    void Say(string s) { Console.WriteLine(s); log.AppendLine(s); }

    using var ec = EmbeddedController.Open(modulePath);

    int WatchedTemp(SmiCommand s) => gpu ? (haveWmi ? (int)s.A3 : ec.ReadStable(0xB7)) : ec.ReadStable(0x58);
    int OtherTemp(SmiCommand s) => gpu ? ec.ReadStable(0x58) : (haveWmi ? (int)s.A3 : ec.ReadStable(0xB7));
    int Rpm(SmiCommand s) => (int)(gpu ? s.A5 : s.A4);

    byte original = ec.ReadStable(targetReg);
    byte output = ec.ReadStable(outputReg);
    byte fanm = ec.ReadStable(Fanm);
    var s0 = haveWmi ? wmi.Execute(SmiCommand.Get(0x0200)) : default;
    int temp = WatchedTemp(s0), other = OtherTemp(s0);
    Say($"{name} fan target ({reg}) = 0x{original:X2}, EC output = {output}%, FANM = 0x{fanm:X2}, {name} = {temp}C, other chip = {other}C, {name} RPM = {Rpm(s0)}");
    if (original != Auto) { Say($"{reg} is not 0xFF (EC automatic). Refusing to write."); return 3; }
    if ((fanm & 1) != 0) { Say("Cooling Boost is ON. Turn it off with Fn+1 first."); return 3; }
    if (!haveWmi) { Say("WMI is needed to watch the fan RPM. Refusing."); return 3; }
    if (temp >= StartMaxC) { Say($"{name} is at {temp}C (limit {StartMaxC}C). Let it cool down first."); return 3; }
    if (other >= OtherAbortC) { Say($"The other chip is at {other}C (limit {OtherAbortC}C). Let it cool down first."); return 3; }

    Console.WriteLine();
    Console.WriteLine($"This will WRITE the EC: {reg} 0xFF -> {string.Join(" -> ", steps)} ({name} fan target %), {DwellSeconds} s per step,");
    Console.WriteLine($"never below {steps[^1]}%, then write 0xFF (automatic) back. It stops at the first step where the RPM stops falling,");
    Console.WriteLine($"if the fan drops under {StallRpm} RPM, if the {name} rises more than {MaxRiseC}C from {temp}C or reaches {AbortC}C.");
    Console.WriteLine("If the PC misbehaves, hold the power button 15-30 s to reset the EC.");
    Console.Write("Type EVET to continue: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "EVET", StringComparison.Ordinal))
    {
        Console.WriteLine("Cancelled. Nothing was written.");
        return 0;
    }

    bool cancelled = false;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled = true; };
    var results = new List<(byte Pct, int Rpm)>();

    try
    {
        int previous = Rpm(wmi.Execute(SmiCommand.Get(0x0200)));
        foreach (byte pct in steps)
        {
            if (cancelled) break;
            ec.WriteByte(targetReg, pct);
            Say($"{DateTime.Now:HH:mm:ss}  wrote {pct} -> {reg}, readback = 0x{ec.ReadStable(targetReg):X2}");

            var tail = new List<int>();
            bool stop = false;
            for (int i = 0; i < DwellSeconds && !cancelled; i++)
            {
                Thread.Sleep(1000);
                var s = wmi.Execute(SmiCommand.Get(0x0200));
                int t = WatchedTemp(s), o = OtherTemp(s), rpm = Rpm(s);
                Say($"{DateTime.Now:HH:mm:ss}  {reg}=0x{ec.ReadStable(targetReg):X2}  out={ec.ReadStable(outputReg)}%  {name} RPM={rpm}  {name}={t}C other={o}C");
                if (i >= DwellSeconds - 3) tail.Add(rpm);
                if (t >= AbortC) { Say($"{name} reached {t}C; aborting and restoring."); stop = true; }
                else if (t > temp + MaxRiseC) { Say($"{name} rose to {t}C (start {temp}C); aborting and restoring."); stop = true; }
                else if (o >= OtherAbortC) { Say($"The other chip reached {o}C; aborting and restoring."); stop = true; }
                else if (rpm < StallRpm) { Say($"{name} fan fell to {rpm} RPM; aborting and restoring."); stop = true; }
                if (stop) break;
            }
            if (stop || tail.Count == 0) break;
            int settled = (int)tail.Average();
            results.Add((pct, settled));
            Say($"        step {pct}%: settled at ~{settled} RPM");
            if (previous - settled < FlatRpm)
            {
                Say($"        RPM stopped following ({previous} -> {settled}); the EC floor is reached at ~{settled} RPM.");
                break;
            }
            previous = settled;
        }
    }
    finally
    {
        // Always put the register back, even after Ctrl+C or an exception.
        try
        {
            ec.WriteByte(targetReg, original);
            Say($"{DateTime.Now:HH:mm:ss}  restored {reg} to 0x{original:X2}, readback = 0x{ec.ReadStable(targetReg):X2}");
        }
        catch (Exception ex)
        {
            Say($"!! RESTORE FAILED ({ex.Message}). Reboot, or hold the power button 15-30 s to reset the EC.");
        }
    }

    for (int i = 0; i < 5; i++)
    {
        Thread.Sleep(2000);
        var s = haveWmi ? wmi.Execute(SmiCommand.Get(0x0200)) : default;
        Say($"{DateTime.Now:HH:mm:ss}  after restore: {reg}=0x{ec.ReadStable(targetReg):X2}  out={ec.ReadStable(outputReg)}%  cpuRPM={s.A4} gpuRPM={s.A5}");
    }
    Say("Summary: " + (results.Count == 0 ? "no step completed" : string.Join(", ", results.Select(r => $"{r.Pct}% -> {r.Rpm} RPM"))));

    string file = $"{(gpu ? "gpufloor" : "fanfloor")}-test-{Stamp()}.txt";
    Save(file, log.ToString());
    Console.WriteLine($"Saved: {Path.Combine(OutDir(), file)}");
    return 0;
}

// --- fanstate: READ-ONLY. Prints the fan target / applied percent (0x2C-0x2F), FANM, both
//     temperatures and both RPMs, once every 3 s for ~30 s. Used after persist-test to see
//     whether the EC keeps a value we wrote, and after a reboot to see whether it was reset. ---
static int FanState(string modulePath, bool haveWmi)
{
    var wmi = new GmWmiGateway();
    var log = new StringBuilder();
    void Say(string s) { Console.WriteLine(s); log.AppendLine(s); }
    using var ec = EmbeddedController.Open(modulePath);
    Say("# 0x2C/0x2D = CPU/GPU fan target (0xFF = EC automatic), 0x2E/0x2F = percent the EC applies");
    for (int i = 0; i < 10; i++)
    {
        var s = haveWmi ? wmi.Execute(SmiCommand.Get(0x0200)) : default;
        Say($"{DateTime.Now:HH:mm:ss}  0x2C=0x{ec.ReadStable(0x2C):X2} 0x2D=0x{ec.ReadStable(0x2D):X2}  out={ec.ReadStable(0x2E)}%/{ec.ReadStable(0x2F)}%  FANM=0x{ec.ReadStable(0xC3):X2}  cpuRPM={s.A4} gpuRPM={s.A5}  cpu={s.A2}C gpu={s.A3}C");
        if (i < 9) Thread.Sleep(3000);
    }
    string file = $"fanstate-{Stamp()}.txt";
    Save(file, log.ToString());
    Console.WriteLine($"Saved: {Path.Combine(OutDir(), file)}");
    return 0;
}

// --- fanreset: writes 0xFF (EC automatic) to 0x2C and 0x2D, and nothing else. That is the
//     safe direction (it hands control back to the EC), so it needs no EVET. Use it to clean
//     up after persist-test, or any time a fan target is stuck. ---
static int FanReset(string modulePath, bool haveWmi)
{
    using var ec = EmbeddedController.Open(modulePath);
    foreach (byte reg in new byte[] { 0x2C, 0x2D })
    {
        byte before = ec.ReadStable(reg);
        if (before != 0xFF) ec.WriteByte(reg, 0xFF);
        Console.WriteLine($"0x{reg:X2}: 0x{before:X2} -> 0x{ec.ReadStable(reg):X2}");
    }
    return 0;
}

// --- persist-test: the one test that DELIBERATELY does not restore. Writes CPU fan target 0x2C = 45
//     (same gates as fanslow-test), watches 10 s, then exits leaving 45 in the EC so that
//     `fanstate` can show whether the EC keeps it, and a reboot whether it survives. Ctrl+C or a
//     thermal abort during the 10 s restores 0xFF. Afterwards run fanreset. ---
static int PersistTest(string modulePath, bool haveWmi)
{
    const byte Reg = 0x2C, Target = 45, Auto = 0xFF, Fanm = 0xC3;
    const int StartMaxC = 60, AbortC = 70, MaxRiseC = 2, OtherAbortC = 85;
    var wmi = new GmWmiGateway();
    var log = new StringBuilder();
    void Say(string s) { Console.WriteLine(s); log.AppendLine(s); }
    using var ec = EmbeddedController.Open(modulePath);

    if (!haveWmi) { Say("WMI is needed to watch temperatures. Refusing."); return 3; }
    var s0 = wmi.Execute(SmiCommand.Get(0x0200));
    byte original = ec.ReadStable(Reg), fanm = ec.ReadStable(Fanm);
    int temp = ec.ReadStable(0x58), gpu = (int)s0.A3;
    Say($"0x2C = 0x{original:X2}, FANM = 0x{fanm:X2}, CPU = {temp}C, GPU = {gpu}C, CPU RPM = {s0.A4}");
    if (original != Auto) { Say("0x2C is not 0xFF. Run fanreset first."); return 3; }
    if ((fanm & 1) != 0) { Say("Cooling Boost is ON. Turn it off with Fn+1 first."); return 3; }
    if (temp >= StartMaxC || gpu >= OtherAbortC) { Say("Too warm to start. Let it cool down first."); return 3; }

    Console.WriteLine();
    Console.WriteLine($"This will WRITE the EC: 0x2C 0xFF -> {Target} (CPU fan ~{Target * 54} RPM) and then EXIT WITHOUT RESTORING.");
    Console.WriteLine("The fan stays slow on purpose, so we can see whether the EC keeps the value. You must then");
    Console.WriteLine("run `fanstate` (read-only), and finish with `fanreset`. Do not leave it set for long or under load.");
    Console.WriteLine($"It restores at once if the CPU rises more than {MaxRiseC}C from {temp}C or reaches {AbortC}C during the first 10 s.");
    Console.Write("Type EVET to continue: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "EVET", StringComparison.Ordinal))
    {
        Console.WriteLine("Cancelled. Nothing was written.");
        return 0;
    }

    bool cancelled = false;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled = true; };
    bool restoreNow = false;
    try
    {
        ec.WriteByte(Reg, Target);
        Say($"{DateTime.Now:HH:mm:ss}  wrote {Target} -> 0x2C, readback = 0x{ec.ReadStable(Reg):X2}");
        for (int i = 0; i < 10 && !cancelled && !restoreNow; i++)
        {
            Thread.Sleep(1000);
            var s = wmi.Execute(SmiCommand.Get(0x0200));
            int t = ec.ReadStable(0x58);
            Say($"{DateTime.Now:HH:mm:ss}  0x2C=0x{ec.ReadStable(Reg):X2}  out={ec.ReadStable(0x2E)}%  cpuRPM={s.A4}  cpu={t}C gpu={s.A3}C");
            if (t >= AbortC || t > temp + MaxRiseC || (int)s.A3 >= OtherAbortC)
            {
                Say($"Temperature gate hit ({t}C); restoring.");
                restoreNow = true;
            }
        }
    }
    catch (Exception ex)
    {
        Say($"Error: {ex.Message}");
        restoreNow = true;
    }
    finally
    {
        if (cancelled || restoreNow)
        {
            try { ec.WriteByte(Reg, original); Say($"restored 0x2C to 0x{original:X2}, readback = 0x{ec.ReadStable(Reg):X2}"); }
            catch (Exception ex) { Say($"!! RESTORE FAILED ({ex.Message}). Run fanreset, or hold the power button 15-30 s."); }
        }
    }

    if (!cancelled && !restoreNow)
    {
        Say("");
        Say("0x2C IS STILL SET TO 45 ON PURPOSE. Next: run `fanstate` to see whether the EC keeps it, then `fanreset`.");
    }
    Save($"persist-test-{Stamp()}.txt", log.ToString());
    return 0;
}

// --- curvelog: READ-ONLY 1 Hz time series (temps, RPM, EC applied %, FANM) for ~10 minutes or until
//     Ctrl+C, saved as CSV. Run it while the EC is in automatic mode (0x2C/0x2D = 0xFF) and put the
//     machine under load to record the STOCK fan curve that the Balanced profile must never undercut. ---
static int CurveLog(string modulePath, bool haveWmi)
{
    if (!haveWmi) { Console.WriteLine("WMI is needed for the sensor block. Refusing."); return 3; }
    var wmi = new GmWmiGateway();
    using var ec = EmbeddedController.Open(modulePath);
    var csv = new StringBuilder();
    csv.AppendLine("time,cpuC,gpuC,cpuRPM,gpuRPM,cpuOut,gpuOut,t2C,t2D,fanm");
    bool stop = false;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop = true; };
    const int Seconds = 600;
    Console.WriteLine($"Logging {Seconds} s (Ctrl+C to stop early). Start your load now. Columns: cpu/gpu C, RPMs, EC applied %.");
    var started = DateTime.Now;
    bool warned = false;
    for (int i = 0; i < Seconds && !stop; i++)
    {
        var s = wmi.Execute(SmiCommand.Get(0x0200));
        byte t2c = ec.ReadStable(0x2C), t2d = ec.ReadStable(0x2D);
        string line = $"{DateTime.Now:HH:mm:ss},{s.A2},{s.A3},{s.A4},{s.A5},{ec.ReadStable(0x2E)},{ec.ReadStable(0x2F)},0x{t2c:X2},0x{t2d:X2},0x{ec.ReadStable(0xC3):X2}";
        csv.AppendLine(line);
        if (i % 5 == 0) Console.WriteLine(line);
        if ((t2c != 0xFF || t2d != 0xFF) && !warned)
        {
            // Expected while the fan service runs (Aşama F load test); the targets are in the CSV.
            Console.WriteLine("  ! a fan target is not 0xFF: this log shows our curve, not the stock EC curve.");
            warned = true;
        }
        Thread.Sleep(1000);
    }
    string file = $"curvelog-{Stamp()}.csv";
    Save(file, csv.ToString());
    Console.WriteLine($"Saved: {Path.Combine(OutDir(), file)}  ({(DateTime.Now - started).TotalSeconds:F0} s)");
    return 0;
}

// --- scan: interactive differential scan of the whole 0x00-0xFF window (READ-ONLY).
//     Takes a snapshot each time you press Enter (type q to finish), then prints every
//     offset whose value is not identical across snapshots. Use it to find the fan
//     target/duty register: snapshot with turbo off, toggle turbo, wait for the RPM to
//     settle, snapshot again. Offsets that follow temperature/RPM noise show up too;
//     the ones that change with turbo but are not RPM/temp are the candidates. ---
static int Scan(string modulePath)
{
    var snapshots = new List<(string Label, int?[] Values)>();
    using var ec = EmbeddedController.Open(modulePath);

    while (true)
    {
        Console.Write($"\nSnapshot #{snapshots.Count + 1} - set the state you want to capture, then press Enter (q = finish): ");
        string? line = Console.ReadLine();
        if (line is null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) break;

        Console.WriteLine("  scanning 256 registers...");
        var values = new int?[256];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int a = 0; a < 256; a++)
        {
            try { values[a] = ec.ReadStable((byte)a, attempts: 3, retries: 1); }
            catch (TimeoutException) { values[a] = null; }
        }
        int dead = values.Count(v => v is null);
        Console.WriteLine($"  done in {sw.Elapsed.TotalSeconds:F1}s, {dead} unreadable");
        string label = string.IsNullOrWhiteSpace(line) ? $"#{snapshots.Count + 1}" : line.Trim();
        snapshots.Add((label, values));
    }

    if (snapshots.Count < 2)
    {
        Console.WriteLine("Need at least two snapshots to compare.");
        return 0;
    }

    var sb = new StringBuilder();
    sb.AppendLine($"# EC scan  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  snapshots: {string.Join(" | ", snapshots.Select(s => s.Label))}");
    sb.AppendLine("# offset  " + string.Join("  ", snapshots.Select((s, i) => $"#{i + 1}")));
    int changed = 0;
    for (int a = 0; a < 256; a++)
    {
        var col = snapshots.Select(s => s.Values[a]).ToArray();
        if (col.Distinct().Count() <= 1) continue;
        changed++;
        sb.AppendLine($"  0x{a:X2}   " + string.Join("  ", col.Select(v => v is null ? "??" : v.Value.ToString("X2"))));
    }
    sb.AppendLine($"# {changed} offset(s) differ between snapshots.");
    Console.WriteLine();
    Console.WriteLine(sb.ToString());
    string scanFile = $"scan-{Stamp()}.txt";
    Save(scanFile, sb.ToString());
    Console.WriteLine($"Saved: {Path.Combine(OutDir(), scanFile)}");
    return 0;
}

// --- msr: the CPU's package power limits (PL1/PL2) and present power, READ-ONLY, through PawnIO's
//     IntelMSR module. Never touches the EC. Run once per power mode to see what each mode sets. ---
static int Msr(string intelMsrPath)
{
    if (!File.Exists(intelMsrPath))
    {
        Console.Error.WriteLine($"IntelMSR module not found at: {intelMsrPath}");
        return 2;
    }
    using var msr = PawnIoSession.Load(File.ReadAllBytes(intelMsrPath));
    ulong Read(uint index) => msr.Execute("ioctl_read_msr", [index], 1)[0];

    var sb = new StringBuilder();
    void Say(string s) { Console.WriteLine(s); sb.AppendLine(s); }

    ulong units = Read(0x606);                       // MSR_RAPL_POWER_UNIT
    double watt = 1.0 / (1 << (int)(units & 0xF));
    double joule = 1.0 / (1 << (int)((units >> 8) & 0x1F));
    double second = 1.0 / (1 << (int)((units >> 16) & 0xF));
    double Window(ulong bits) => Math.Pow(2, bits & 0x1F) * (1 + ((bits >> 5) & 0x3) / 4.0) * second;

    ulong limit = Read(0x610);                       // MSR_PKG_POWER_LIMIT
    Say($"# CPU power limits  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    Say($"0x606 units  = 0x{units:X16}  ({watt} W, {joule:G4} J, {second:G4} s)");
    Say($"0x610 limit  = 0x{limit:X16}  locked={(limit >> 63) & 1}");
    Say($"  PL1 {(limit & 0x7FFF) * watt,6:F1} W  enabled={(limit >> 15) & 1}  clamp={(limit >> 16) & 1}  window {Window(limit >> 17):F1} s");
    Say($"  PL2 {((limit >> 32) & 0x7FFF) * watt,6:F1} W  enabled={(limit >> 47) & 1}  clamp={(limit >> 48) & 1}  window {Window(limit >> 49):F3} s");
    ulong info = Read(0x614);                        // MSR_PKG_POWER_INFO
    Say($"0x614 info   = 0x{info:X16}  (design TDP {(info & 0x7FFF) * watt:F1} W)");

    // The same limits mirrored in MCHBAR (+0x59A0); the CPU obeys the lower of the two copies.
    string mchbarPath = Path.Combine(Path.GetDirectoryName(intelMsrPath)!, "IntelMCHBAR.bin");
    try
    {
        using var mchbar = PawnIoSession.Load(File.ReadAllBytes(mchbarPath));
        ulong mmio = mchbar.Execute("ioctl_read_qword", [0x59A0], 1)[0];
        Say($"MCHBAR+0x59A0 = 0x{mmio:X16}  locked={(mmio >> 63) & 1}");
        Say($"  PL1 {(mmio & 0x7FFF) * watt,6:F1} W  enabled={(mmio >> 15) & 1}   PL2 {((mmio >> 32) & 0x7FFF) * watt,6:F1} W  enabled={(mmio >> 47) & 1}");
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException)
    {
        Say($"MCHBAR read failed: {ex.Message}");
    }

    // Present package power from the energy counter over two seconds.
    uint e0 = (uint)Read(0x611);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    Thread.Sleep(2000);
    uint e1 = (uint)Read(0x611);
    Say($"package power now ~{unchecked(e1 - e0) * joule / sw.Elapsed.TotalSeconds:F1} W");

    string file = $"msr-{Stamp()}.txt";
    Save(file, sb.ToString());
    Console.WriteLine($"Saved: {Path.Combine(OutDir(), file)}");
    return 0;
}

// --- cputemp: the CPU's own package temperature, to compare with the WMI value the service records
//     (history) — WMI refreshes it only about every 16.5 s. READ-ONLY. ---
static int CpuTemp(string intelMsrPath, int seconds)
{
    if (!File.Exists(intelMsrPath))
    {
        Console.Error.WriteLine($"IntelMSR module not found at: {intelMsrPath}");
        return 2;
    }
    using var msr = PawnIoSession.Load(File.ReadAllBytes(intelMsrPath));
    var sensor = new Excalibur.Core.Fan.MsrCpuTemperature(new ProbeMsr(msr));
    var sb = new StringBuilder();
    void Say(string s) { Console.WriteLine(s); sb.AppendLine(s); }

    Say($"# CPU package temperature (MSR 0x1B1), every 250 ms for {seconds} s");
    var end = DateTime.Now.AddSeconds(seconds);
    while (DateTime.Now < end)
    {
        Say($"{DateTime.Now:HH:mm:ss.fff}  {sensor.Read()?.ToString() ?? "?"}");
        Thread.Sleep(250);
    }
    string file = $"cputemp-{Stamp()}.txt";
    Save(file, sb.ToString());
    Console.WriteLine($"Saved: {Path.Combine(OutDir(), file)}");
    return 0;
}

// --- msr-test: does lowering the MSR copy of the limits take effect? Only the PL1/PL2 power fields
//     of 0x610 change (enable, clamp and window bits are kept), only downward, only after EVET, and the
//     original value is written back even on Ctrl+C or an error. Never touches the EC. ---
static int MsrTest(string intelMsrPath)
{
    const uint Units = 0x606, Limit = 0x610, Energy = 0x611;
    const int Pl1Watts = 15, Pl2Watts = 20;
    using var msr = PawnIoSession.Load(File.ReadAllBytes(intelMsrPath));
    ulong Read(uint index) => msr.Execute("ioctl_read_msr", [index], 1)[0];
    void Write(uint index, ulong value) => msr.Execute("ioctl_write_msr", [index, value], 0);

    var log = new StringBuilder();
    void Say(string s) { Console.WriteLine(s); log.AppendLine(s); }

    ulong units = Read(Units);
    double watt = 1.0 / (1 << (int)(units & 0xF));
    double joule = 1.0 / (1 << (int)((units >> 8) & 0x1F));
    ulong original = Read(Limit);
    ulong lowered = (original & ~0x7FFFUL & ~(0x7FFFUL << 32))
        | (ulong)(Pl1Watts / watt) | ((ulong)(Pl2Watts / watt) << 32);

    Say($"# MSR limit test  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    Say($"original 0x610 = 0x{original:X16}  PL1 {(original & 0x7FFF) * watt} W, PL2 {((original >> 32) & 0x7FFF) * watt} W");
    Console.WriteLine();
    Console.WriteLine($"This writes PL1 {Pl1Watts} W / PL2 {Pl2Watts} W to MSR 0x610 for 40 s, then writes the original back.");
    Console.WriteLine("Start a CPU load now (a game or a stress test) so the effect shows. Lower limits only slow the CPU.");
    Console.Write("Type EVET to continue: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "EVET", StringComparison.Ordinal))
    {
        Console.WriteLine("Cancelled. Nothing was written.");
        return 0;
    }

    bool cancelled = false;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled = true; };
    var clock = System.Diagnostics.Stopwatch.StartNew();
    (uint E, double T) last = ((uint)Read(Energy), 0);
    string Power()
    {
        uint e = (uint)Read(Energy);
        double t = clock.Elapsed.TotalSeconds;
        string w = $"{unchecked(e - last.E) * joule / (t - last.T),5:F1} W";
        last = (e, t);
        return w;
    }

    for (int i = 0; i < 5 && !cancelled; i++) { Thread.Sleep(1000); Say($"{DateTime.Now:HH:mm:ss}  before   {Power()}"); }
    try
    {
        Write(Limit, lowered);
        Say($"{DateTime.Now:HH:mm:ss}  wrote 0x{lowered:X16}, readback 0x{Read(Limit):X16}");
        for (int i = 0; i < 40 && !cancelled; i++) { Thread.Sleep(1000); Say($"{DateTime.Now:HH:mm:ss}  limited  {Power()}"); }
    }
    finally
    {
        try
        {
            Write(Limit, original);
            Say($"{DateTime.Now:HH:mm:ss}  restored, readback 0x{Read(Limit):X16}");
        }
        catch (Exception ex)
        {
            Say($"!! RESTORE FAILED ({ex.Message}). A restart puts the BIOS values back.");
        }
    }
    for (int i = 0; i < 10; i++) { Thread.Sleep(1000); Say($"{DateTime.Now:HH:mm:ss}  after    {Power()}"); }

    string file = $"msr-test-{Stamp()}.txt";
    Save(file, log.ToString());
    Console.WriteLine($"Saved: {Path.Combine(OutDir(), file)}");
    return 0;
}

// --- led-test: the keyboard light through WMI 0x0100 only (the firmware's own method; no EC port access). ---
static int LedTest()
{
    var light = new Excalibur.Core.Lighting.KeyboardLight(new GmWmiGateway());
    var log = new StringBuilder();
    void Say(string s) { Console.WriteLine(s); log.AppendLine(s); }

    uint before = light.ReadRaw();
    var start = Excalibur.Core.Lighting.LightSetting.Decode(before);
    Say($"# keyboard light test  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    Say($"GET 0x0100 a2 = 0x{before:X8} ({start}), key brightness (0x0200 a6) = {light.ReadKeyBrightness()}");
    Console.WriteLine();
    Console.WriteLine("This changes only the keyboard light, one step every 4 s. Note what the keyboard shows at each step.");
    Console.Write("Type EVET to continue: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "EVET", StringComparison.Ordinal))
    {
        Console.WriteLine("Cancelled. Nothing was written.");
        return 0;
    }

    bool cancelled = false;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled = true; };
    static Excalibur.Core.Lighting.LightSetting S(Excalibur.Core.Lighting.LightEffect e, int b, int r, int g, int bl) => new(e, b, r, g, bl);
    var st = Excalibur.Core.Lighting.LightEffect.Static;
    var K = Excalibur.Core.Lighting.LightZone.Keyboard;
    (string Label, (Excalibur.Core.Lighting.LightZone Zone, Excalibur.Core.Lighting.LightSetting Setting)[] Sends)[] steps =
    [
        ("1 whole keyboard RED, brightness 2", [(K, S(st, 2, 255, 0, 0))]),
        ("2 whole keyboard GREEN", [(K, S(st, 2, 0, 255, 0))]),
        ("3 whole keyboard BLUE", [(K, S(st, 2, 0, 0, 255))]),
        ("4 zones: zone 5 RED, zone 4 GREEN, zone 3 BLUE (which is left/middle/right?)",
            [(Excalibur.Core.Lighting.LightZone.KeyboardLeft, S(st, 2, 255, 0, 0)),
             (Excalibur.Core.Lighting.LightZone.KeyboardMiddle, S(st, 2, 0, 255, 0)),
             (Excalibur.Core.Lighting.LightZone.KeyboardRight, S(st, 2, 0, 0, 255))]),
        ("5 whole keyboard WHITE, brightness 1", [(K, S(st, 1, 255, 255, 255))]),
        ("6 whole keyboard WHITE, brightness 0", [(K, S(st, 0, 255, 255, 255))]),
        ("7 BREATHING cyan, brightness 2", [(K, S(Excalibur.Core.Lighting.LightEffect.Breathing, 2, 0, 255, 255))]),
        ("8 COLOUR CYCLE, brightness 2", [(K, S(Excalibur.Core.Lighting.LightEffect.ColorCycle, 2, 255, 0, 128))]),
        ("9 OFF", [(K, Excalibur.Core.Lighting.LightSetting.Off)]),
    ];
    try
    {
        foreach (var (label, sends) in steps)
        {
            if (cancelled) break;
            foreach (var (zone, setting) in sends) light.Set(zone, setting);
            Say($"{DateTime.Now:HH:mm:ss}  {label}  -> GET 0x0100 a2 = 0x{light.ReadRaw():X8}, key brightness {light.ReadKeyBrightness()}");
            Thread.Sleep(label.StartsWith('7') || label.StartsWith('8') ? 6000 : 4000);
        }
    }
    finally
    {
        try
        {
            // GET's answer decodes as a setting on the stock software's side (alpha + RGB); fall back to static white.
            var restore = start.Validate().Count == 0 ? start : S(st, Math.Max(1, light.ReadKeyBrightness()), 255, 255, 255);
            light.Forget();
            light.Set(K, restore);
            Say($"{DateTime.Now:HH:mm:ss}  restored {restore}  -> GET 0x0100 a2 = 0x{light.ReadRaw():X8}");
        }
        catch (Exception ex) { Say($"!! RESTORE FAILED ({ex.Message}). The keyboard light key still works."); }
    }

    string file = $"led-test-{Stamp()}.txt";
    Save(file, log.ToString());
    Console.WriteLine($"Saved: {Path.Combine(OutDir(), file)}");
    return 0;
}

// --- diag: why do back-to-back reads time out? Measures status bytes and the
//     success rate / latency of repeated reads of one known-good register (RTMP). ---
static int Diag(string modulePath)
{
    var sb = new StringBuilder();
    void Say(string s) { Console.WriteLine(s); sb.AppendLine(s); }

    using var ec = EmbeddedController.Open(modulePath);

    Say("# raw status (0x66) x20, 50 ms apart   [bit0=OBF bit1=IBF]");
    var seen = new Dictionary<byte, int>();
    for (int i = 0; i < 20; i++)
    {
        byte st = ec.ReadStatus();
        seen[st] = seen.GetValueOrDefault(st) + 1;
        Thread.Sleep(50);
    }
    Say("  " + string.Join("  ", seen.Select(kv => $"0x{kv.Key:X2} x{kv.Value}")));

    Say("# step trace of 12 raw RTMP(0x58) reads, 300 ms apart (no retry/recovery):");
    for (int i = 0; i < 12; i++)
    {
        Say("  " + ec.TraceRead(0x58));
        Thread.Sleep(300);
    }

    foreach (int gapMs in new[] { 0, 50, 250 })
    {
        int ok = 0, fail = 0;
        var values = new Dictionary<byte, int>();
        var sw = new System.Diagnostics.Stopwatch();
        long totalMs = 0;
        for (int i = 0; i < 20; i++)
        {
            sw.Restart();
            try { byte v = ec.ReadByte(0x58); ok++; values[v] = values.GetValueOrDefault(v) + 1; }
            catch (TimeoutException) { fail++; }
            totalMs += sw.ElapsedMilliseconds;
            if (gapMs > 0) Thread.Sleep(gapMs);
        }
        Say($"# RTMP(0x58) x20, gap={gapMs}ms:  ok={ok} fail={fail}  avg={totalMs / 20}ms  values: " +
            string.Join(" ", values.Select(kv => $"{kv.Key}x{kv.Value}")));
    }

    Save($"diag-{Stamp()}.txt", sb.ToString());
    return 0;
}

// --- watch: live differential poll of the candidate registers ---
static int Watch(string modulePath, bool haveWmi, (byte Addr, string Name)[] candidates)
{
    var wmi = new GmWmiGateway();
    var log = new StringBuilder();
    byte[]? previous = null;
    int cycle = 0;

    string header = "time      cpu gpu  cpuRPM gpuRPM | " +
                    string.Join(" ", candidates.Select(c => c.Name));
    Console.WriteLine(header);
    Console.WriteLine("(printing a row on every change; Ctrl+C to stop)");
    log.AppendLine($"# EC watch  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    log.AppendLine("# " + header);

    using var stop = new ManualResetEventSlim(false);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

    using (var ec = EmbeddedController.Open(modulePath))
    {
        while (!stop.IsSet)
        {
            var now = new byte[candidates.Length];
            for (int i = 0; i < candidates.Length; i++)
            {
                try { now[i] = ec.ReadStable(candidates[i].Addr); }
                catch (TimeoutException) { now[i] = 0xFF; } // mark unreadable this cycle
            }

            SmiCommand s = haveWmi ? wmi.Execute(SmiCommand.Get(0x0200)) : default;
            bool changed = previous is null || !now.AsSpan().SequenceEqual(previous);

            if (changed || cycle % 20 == 0)
            {
                string cells = string.Join(" ", candidates.Select((c, i) =>
                {
                    string hex = now[i].ToString("X2");
                    bool diff = previous is not null && now[i] != previous[i];
                    return diff ? $"[{hex}]" : $" {hex} ";
                }));
                string row = haveWmi
                    ? $"{DateTime.Now:HH:mm:ss}  {s.A2,3} {s.A3,3}  {FanRpm(s, 0),6} {FanRpm(s, 1),6} | {cells}"
                    : $"{DateTime.Now:HH:mm:ss}   -   -       -      - | {cells}";
                Console.WriteLine(row);
                log.AppendLine(row);
                previous = now;
            }

            cycle++;
            stop.Wait(TimeSpan.FromMilliseconds(750));
        }
    }

    string file = $"watch-{Stamp()}.txt";
    Save(file, log.ToString());
    Console.WriteLine($"\nSaved: {Path.Combine(OutDir(), file)}");
    return 0;
}

// Fan RPM as reported by the firmware's own sensor block. GET 0x0200 returns the
// BIOS-decoded values directly: A4 = CPU fan RPM, A5 = GPU fan RPM (see docs/05).
static int FanRpm(SmiCommand s, int index) => index == 0 ? (int)s.A4 : (int)s.A5;

// --- focused (or full) read-only dump, annotated and saved ---
static int FullDump(string label, string modulePath, bool haveWmi, SmiCommand sensors,
                    (byte Addr, string Name)[] candidates)
{
    bool full = label.Equals("full", StringComparison.OrdinalIgnoreCase);

    string sensorLine = haveWmi
        ? $"WMI sensors: CPU={sensors.A2}C GPU={sensors.A3}C CPUfan={FanRpm(sensors, 0)}rpm GPUfan={FanRpm(sensors, 1)}rpm"
        : "WMI RW_GMWMI not available";

    byte[] map;
    IReadOnlyList<byte> unreadable;
    using (var ec = EmbeddedController.Open(modulePath))
        map = full
            ? ec.Dump(out unreadable)
            : ec.Dump(candidates.Select(c => c.Addr), out unreadable);

    var sb = new StringBuilder();
    sb.AppendLine($"# EC dump  label={label}  mode={(full ? "full-256" : "focused-candidates")}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    sb.AppendLine($"# {sensorLine}");

    if (full)
    {
        sb.AppendLine("# offset  00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F");
        for (int row = 0; row < 256; row += 16)
        {
            sb.Append($"  0x{row:X2}:  ");
            for (int col = 0; col < 16; col++)
            {
                sb.Append($"{map[row + col]:X2} ");
                if (col == 7) sb.Append(' ');
            }
            sb.AppendLine();
        }
    }

    sb.AppendLine("# candidate registers:");
    foreach (var (addr, name) in candidates)
    {
        bool bad = unreadable.Contains(addr);
        sb.AppendLine($"#   0x{addr:X2} {name,-16} = {(bad ? "  ? (timed out)" : $"{map[addr],3} (0x{map[addr]:X2})")}");
    }
    if (unreadable.Count > 0)
        sb.AppendLine("# unreadable (timed out): " + string.Join(" ", unreadable.Select(a => $"0x{a:X2}")));

    string file = $"{label}-{Stamp()}.txt";
    Save(file, sb.ToString());
    Console.WriteLine(sb.ToString());
    Console.WriteLine($"Saved: {Path.Combine(OutDir(), file)}");
    return 0;
}

static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");

static string OutDir() => Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "analysis", "ec"));

static void Save(string fileName, string content)
{
    string dir = OutDir();
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, fileName), content);
}

static bool IsElevated()
{
    using var id = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
}

sealed class ProbeMsr(PawnIoSession msr) : Excalibur.Core.Power.ICpuPowerRegisters
{
    public ulong ReadMsr(uint index) => msr.Execute("ioctl_read_msr", [index], 1)[0];
    public void WriteMsr(uint index, ulong value) => throw new InvalidOperationException("cputemp is read-only.");
    public ulong? ReadMchbarLimit() => null;
}
