using System.Globalization;

namespace Excalibur.Core.Power;

/// <summary>
/// The CPU's own power-limit values from before the app's first write, kept on disk for one boot. Held only in memory, a
/// service that died with a limit written (15/30 W) came back taking that limit for the original, and "no limit" kept the
/// CPU throttled until a restart (code review 2026-11-14, B1). A restart puts the firmware's values back, so a file from an
/// earlier boot is deleted, not used. Failures to read or write the file only lose this safeguard (as before it existed).
/// </summary>
public sealed class SavedCpuLimits(string path, Func<string?>? currentBoot = null)
{
    public const string FileName = "cpu-limit-original.txt";

    private readonly Func<string?> _boot = currentBoot ?? BootSession.Current;

    /// <summary>The values saved in this boot, or null.</summary>
    public ulong[]? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length >= 2 && _boot() is { } boot && lines[0].Trim() == boot)
            {
                var values = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => ulong.Parse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
                if (values.Length > 0) return values;
            }
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException) { }
        return null;
    }

    /// <summary>Saves the values before the first write; without a boot identity nothing is saved.</summary>
    public void Save(params ulong[] values)
    {
        if (_boot() is not { } boot) return;
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllLines(tmp, [boot, string.Join(' ', values.Select(v => v.ToString("X", CultureInfo.InvariantCulture)))]);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The original is back in the register: nothing left to undo.</summary>
    public void Clear()
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>An identity of the current boot (Linux: the kernel's boot_id; Windows: the boot counter), or null.</summary>
public static class BootSession
{
    public static string? Current()
    {
        try
        {
#if LINUX
            return "linux:" + File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
#else
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
            return key?.GetValue("BootId") is int id ? "windows:" + id.ToString(CultureInfo.InvariantCulture) : null;
#endif
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }
}
