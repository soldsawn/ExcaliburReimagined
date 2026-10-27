using System.Globalization;

namespace Excalibur.Core.Power;

/// <summary>What a power mode allows on this machine; null when not known.</summary>
public sealed record ModeWatts(PowerMode Mode, int? CpuWatts, int? GpuWatts);

/// <summary>
/// What each power mode allows on this machine, measured on the wall a few seconds after the service sent it: the GPU
/// limit the driver enforces and the CPU's long-term limit in the MCHBAR copy (the user's own CPU limit sits in the MSR
/// copy, and the GPU limit is a clock lock, so neither shows here). <see cref="PowerModeInfo"/> holds the G870's values;
/// another laptop's card differs (a 140 W RTX 4070 in Office would have read as High performance). Until a mode is
/// measured it is the G870's only while every measured mode matched the G870, or nothing was measured yet.
/// </summary>
public sealed class PowerModeWatts(string? path = null)
{
    public const string FileName = "power-modes.txt";
    private const int Tolerance = 2;

    private readonly Dictionary<PowerMode, (int? Cpu, int Gpu)> _measured = Load(path);
    private readonly object _gate = new();

    /// <summary>Stores a measurement; true when it was new or changed.</summary>
    public bool Learn(PowerMode mode, int gpuWatts, int? cpuWatts)
    {
        lock (_gate)
        {
            if (_measured.TryGetValue(mode, out var known) && known == (cpuWatts, gpuWatts)) return false;
            _measured[mode] = (cpuWatts, gpuWatts);
            Save();
            return true;
        }
    }

    /// <summary>Every mode: measured, or the G870's while this machine looks like one, else unknown.</summary>
    public IReadOnlyList<ModeWatts> All
    {
        get
        {
            lock (_gate)
            {
                bool g870 = LooksLikeG870();
                return Enum.GetValues<PowerMode>().Select(m => _measured.TryGetValue(m, out var w) ? new ModeWatts(m, w.Cpu, w.Gpu)
                    : _measured.Count > 0 && g870 ? new ModeWatts(m, PowerModeInfo.CpuWatts(m), PowerModeInfo.GpuWatts(m))
                    : new ModeWatts(m, null, null)).ToList();
            }
        }
    }

    /// <summary>
    /// The mode whose GPU limit is <paramref name="watts"/>: measured modes first, the G870's value for the others unless a
    /// measured mode already has it. Null when none matches, or several do (modes that differ only in the CPU).
    /// </summary>
    public PowerMode? FromGpuWatts(int watts)
    {
        lock (_gate)
        {
            var matches = Enum.GetValues<PowerMode>().Where(m =>
                _measured.TryGetValue(m, out var w) ? Near(w.Gpu, watts)
                : Near(PowerModeInfo.GpuWatts(m), watts) && !_measured.Values.Any(v => Near(v.Gpu, watts))).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }
    }

    private static bool Near(int a, int b) => Math.Abs(a - b) <= Tolerance;

    private bool LooksLikeG870() => _measured.All(kv =>
        Near(kv.Value.Gpu, PowerModeInfo.GpuWatts(kv.Key)) && (kv.Value.Cpu is null || kv.Value.Cpu == PowerModeInfo.CpuWatts(kv.Key)));

    // One line per mode: "Gaming 85 45" (GPU, CPU; "-" when the CPU could not be read).
    private static Dictionary<PowerMode, (int? Cpu, int Gpu)> Load(string? path)
    {
        var result = new Dictionary<PowerMode, (int? Cpu, int Gpu)>();
        if (path is null) return result;
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 3 && Enum.TryParse(p[0], out PowerMode mode) && Enum.IsDefined(mode)
                    && int.TryParse(p[1], CultureInfo.InvariantCulture, out int gpu) && gpu > 0)
                    result[mode] = (int.TryParse(p[2], CultureInfo.InvariantCulture, out int cpu) ? cpu : null, gpu);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    private void Save()
    {
        if (path is null) return;
        try { File.WriteAllLines(path, _measured.Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} {kv.Value.Gpu} {kv.Value.Cpu?.ToString() ?? "-"}"))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
