using System.Diagnostics;
using System.Management;

namespace Excalibur.Core.Graphics;

/// <summary>What a Discrete change needs from Windows, done as the stock Control Center does it.</summary>
public static class WindowsGraphicsSystem
{
    /// <summary>
    /// True when BitLocker protects the system drive: Win32_EncryptableVolume.ProtectionStatus 1, 3 or 5 for the volume
    /// of drive C: (the stock DetectBitLocker; its list kept as is). A query that fails counts as not protected and is
    /// logged (Windows editions without the class).
    /// </summary>
    public static bool BitLockerOn(Action<string> log)
    {
        try
        {
            string? volume = null;
            using (var volumes = new ManagementObjectSearcher("SELECT DeviceID, DriveLetter FROM Win32_Volume WHERE DriveLetter = 'C:'"))
                foreach (ManagementObject v in volumes.Get())
                    using (v) volume ??= v["DeviceID"] as string;
            if (volume is null) return false;
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\root\CIMV2\Security\MicrosoftVolumeEncryption"),
                new SelectQuery("Win32_EncryptableVolume"));
            foreach (ManagementObject e in searcher.Get())
            {
                using (e)
                {
                    if (e["DeviceID"] as string != volume) continue;
                    return Convert.ToString(e["ProtectionStatus"]) is "1" or "3" or "5";
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            log($"graphics: BitLocker state not read ({ex.Message}); taken as off");
            return false;
        }
    }

    /// <summary>Restarts Windows the stock software's way: 3 s, then shutdown -r -t 0 (the user confirmed and was told to save).</summary>
    public static void Restart(Action<string> log)
    {
        log("graphics: restarting Windows in 3 s");
        Thread.Sleep(TimeSpan.FromSeconds(3));
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "-r -t 0")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GraphicsException("graphics_restart", $"Windows could not be restarted: {ex.Message}", ex);
        }
    }
}
