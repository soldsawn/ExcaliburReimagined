using System.Diagnostics;

namespace Excalibur.Linux.Ui;

/// <summary>
/// Chip and machine names on Linux (the Windows window reads them from the registry): the CPU from /proc/cpuinfo, the
/// NVIDIA GPU from its driver (or lspci), the model and BIOS version from the firmware tables in /sys/class/dmi/id.
/// </summary>
internal static class HardwareNames
{
    public static readonly string? Cpu = ReadCpu();
    public static readonly string? Gpu = ReadGpu();
    public static readonly string? Model = Read("/sys/class/dmi/id/product_name");
    public static readonly string? Bios = Read("/sys/class/dmi/id/bios_version");

    private static string? ReadCpu()
    {
        try
        {
            return File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))
                ?.Split(':', 2)[1].Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadGpu()
    {
        try
        {
            // The NVIDIA driver names the card itself ("Model: NVIDIA GeForce RTX 4060 Laptop GPU").
            const string gpus = "/proc/driver/nvidia/gpus";
            if (Directory.Exists(gpus))
                foreach (var dir in Directory.GetDirectories(gpus))
                    if (File.ReadLines(Path.Combine(dir, "information")).FirstOrDefault(l => l.StartsWith("Model:", StringComparison.Ordinal)) is { } model)
                        return model[6..].Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            // Without the driver (nouveau): lspci's description of the NVIDIA display controller.
            using var p = Process.Start(new ProcessStartInfo("lspci") { RedirectStandardOutput = true, UseShellExecute = false });
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            var line = output.Split('\n').FirstOrDefault(l => l.Contains("NVIDIA", StringComparison.Ordinal)
                && (l.Contains("VGA", StringComparison.Ordinal) || l.Contains("3D controller", StringComparison.Ordinal)));
            if (line?.Split(": ", 2) is not [_, var desc]) return null;
            // "NVIDIA Corporation AD107M [GeForce RTX 4060 Max-Q / Mobile] (rev a1)": the marketing name is in the brackets.
            int open = desc.LastIndexOf('['), close = desc.LastIndexOf(']');
            return open >= 0 && close > open ? "NVIDIA " + desc[(open + 1)..close].Trim() : desc.Replace("NVIDIA Corporation", "NVIDIA").Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    private static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
