using Microsoft.Win32;

namespace Excalibur.App.Ui;

/// <summary>Chip names from the registry (instant, no WMI): the CPU brand string, the NVIDIA adapter, the model and BIOS version.</summary>
internal static class HardwareNames
{
    public static readonly string? Cpu = Read(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim();

    public static readonly string? Gpu = FindGpu();

    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    public static readonly string? Model = Read(BiosKey, "SystemProductName")?.Trim();
    public static readonly string? Bios = Read(BiosKey, "BIOSVersion")?.Trim();

    private static string? FindGpu()
    {
        // The display adapter device class; each numbered subkey is one adapter.
        const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(displayClass);
            return key?.GetSubKeyNames()
                .Select(n => Read($@"{displayClass}\{n}", "DriverDesc"))
                .FirstOrDefault(d => d?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? Read(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
