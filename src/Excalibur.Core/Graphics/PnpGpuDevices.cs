using System.Diagnostics;
using System.Management;

namespace Excalibur.Core.Graphics;

/// <summary>
/// The NVIDIA GPU (PCI\VEN_10DE…, class Display) and its platform framework device (ACPI\NVDA0820) in Windows, switched
/// with pnputil. The stock Control Center switches the same two (its VGA.ini lists them for this model, VGA14), in this
/// order, a second apart. Found by vendor and class rather than a fixed list, so another NVIDIA card is found too; the
/// NVIDIA HD audio device is left alone, as the stock software does.
/// </summary>
public sealed class PnpGpuDevices(Action<string> log) : IGpuDevices
{
    private const int Disabled = 22; // ConfigManagerErrorCode: CM_PROB_DISABLED
    private static readonly TimeSpan Between = TimeSpan.FromSeconds(1), PnputilTimeout = TimeSpan.FromSeconds(60);

    private sealed record Device(string Id, bool Enabled);

    public bool? Enabled => Find().Gpu?.Enabled;

    public void Disable() => Switch(enable: false);

    public void Enable() => Switch(enable: true);

    private void Switch(bool enable)
    {
        var (gpu, framework) = Find();
        if (gpu is null) throw new GraphicsException("graphics_devices", "No NVIDIA GPU was found.");
        foreach (var device in new[] { gpu, framework })
        {
            if (device is null || device.Enabled == enable) continue;
            Pnputil(enable ? "/enable-device" : "/disable-device", device.Id);
            Thread.Sleep(Between);
        }
    }

    private static (Device? Gpu, Device? Framework) Find()
    {
        Device? gpu = null, framework = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PNPDeviceID, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'PCI\\\\VEN_10DE%' OR PNPDeviceID LIKE 'ACPI\\\\NVDA0820%'");
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    string id = o["PNPDeviceID"] as string ?? "";
                    bool enabled = Convert.ToUInt32(o["ConfigManagerErrorCode"] ?? 0u) != Disabled;
                    if (id.StartsWith(@"ACPI\NVDA0820", StringComparison.OrdinalIgnoreCase)) framework ??= new Device(id, enabled);
                    else if (string.Equals(o["PNPClass"] as string, "Display", StringComparison.OrdinalIgnoreCase)) gpu ??= new Device(id, enabled);
                }
            }
        }
        catch (ManagementException ex) { throw new GraphicsException("graphics_devices", $"Devices could not be listed: {ex.Message}", ex); }
        return (gpu, framework);
    }

    private void Pnputil(string verb, string id)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), $"{verb} \"{id}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(info) ?? throw new GraphicsException("graphics_devices", "pnputil did not start.");
        // Read while it runs, so a full pipe cannot hold it up.
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(PnputilTimeout))
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            throw new GraphicsException("graphics_devices", $"pnputil {verb} did not finish in {PnputilTimeout.TotalSeconds:F0} s.");
        }
        string text = (output.Result + errors.Result).Trim().ReplaceLineEndings(" ");
        log($"graphics: pnputil {verb} {id}: exit {p.ExitCode}");
        // 3010: done, a restart would complete it.
        if (p.ExitCode is not (0 or 3010)) throw new GraphicsException("graphics_devices", $"pnputil {verb} failed ({p.ExitCode}): {text}");
    }
}
