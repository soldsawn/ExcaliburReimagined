using System.Diagnostics;
using System.Management;
using Excalibur.Core.Hardware;

namespace Excalibur.Core.Gpu;

/// <summary>
/// The real graphics mode hardware: WMI 0x0203 (GET a2 = mode, a3 = external display; SET a2 = mode), the NVIDIA
/// devices through pnputil (the display adapter PCI\VEN_10DE… and the platform framework ACPI\NVDA0820, the two the
/// stock software's VGA.ini lists; the HDMI audio stays), and BitLocker from Win32_EncryptableVolume. Needs the
/// service's rights (pnputil, BitLocker).
/// </summary>
public sealed class WmiGpuHardware(ISmiChannel wmi) : IGpuHardware
{
    private const ushort DisplayFunction = 0x0203;
    private static readonly TimeSpan PnpTimeout = TimeSpan.FromSeconds(30);

    public (GpuMode? Mode, bool Hdmi) Read()
    {
        var r = wmi.Execute(SmiCommand.Get(DisplayFunction));
        GpuMode? mode = Enum.IsDefined(typeof(GpuMode), (int)r.A2) ? (GpuMode)r.A2 : null;
        return (mode, r.A3 == 1);
    }

    public void Write(GpuMode mode)
    {
        var c = SmiCommand.Set(DisplayFunction);
        c.A2 = (uint)mode;
        wmi.Execute(c);
    }

    public void SetNvidiaEnabled(bool enabled)
    {
        var ids = NvidiaDevices();
        if (ids.Count == 0) throw new GpuException("no NVIDIA graphics device found");
        foreach (var id in ids)
        {
            Pnputil(enabled ? "/enable-device" : "/disable-device", id);
            Thread.Sleep(1000); // the stock software waits between the two as well
        }
    }

    /// <summary>The display adapter first, then the framework (the stock order); present devices only.</summary>
    private static List<string> NvidiaDevices()
    {
        var gpus = new List<string>();
        var framework = new List<string>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, PNPClass FROM Win32_PnPEntity WHERE DeviceID LIKE 'PCI\\\\VEN_10DE%' OR DeviceID LIKE 'ACPI\\\\NVDA0820%'");
        foreach (ManagementObject o in searcher.Get())
            using (o)
            {
                string id = (string)o["DeviceID"];
                if (id.StartsWith(@"ACPI\NVDA0820", StringComparison.OrdinalIgnoreCase)) framework.Add(id);
                else if (string.Equals(o["PNPClass"] as string, "Display", StringComparison.OrdinalIgnoreCase)) gpus.Add(id);
            }
        return [.. gpus, .. framework];
    }

    private static void Pnputil(string verb, string id)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), $"{verb} \"{id}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new GpuException("pnputil could not be started");
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(PnpTimeout)) { try { p.Kill(); } catch (InvalidOperationException) { } throw new GpuException($"pnputil {verb} timed out"); }
        // 3010 = done, a restart completes it.
        if (p.ExitCode is not (0 or 3010)) throw new GpuException($"pnputil {verb} {id} failed ({p.ExitCode}): {output.Trim()}");
    }

    public bool? BitLockerOff()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"root\CIMV2\Security\MicrosoftVolumeEncryption"),
                new ObjectQuery("SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = 'C:'"));
            foreach (ManagementObject v in searcher.Get())
                using (v)
                    // 0 = off, 1 = on, 2 = unknown.
                    return v["ProtectionStatus"] is uint status ? status == 0 ? true : status == 1 ? false : null : null;
            return null;
        }
        catch (ManagementException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
