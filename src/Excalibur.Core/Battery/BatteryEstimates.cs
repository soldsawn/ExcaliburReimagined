using System.Globalization;
using Excalibur.Core.Power;

namespace Excalibur.Core.Battery;

/// <summary>The average power on battery with one power mode and fan profile, over all its kept intervals.</summary>
public sealed record BatteryEstimate(PowerMode? Mode, string Profile, double Minutes, double Watts)
{
    /// <summary>Hours a full battery of <paramref name="fullMWh"/> lasts at this average; null without a usable average.</summary>
    public double? Hours(int fullMWh) => Watts > 0.1 ? fullMWh / 1000.0 / Watts : null;
}

/// <summary>
/// Averages the kept battery intervals per power mode and fan profile, from the csv log (the last <see cref="BatteryCsvLog.KeepDays"/>
/// days, read at start) and each new interval. An estimate is trusted from <see cref="NeededMinutes"/> of data on.
/// </summary>
public sealed class BatteryEstimates
{
    public const double NeededMinutes = 30;

    private readonly object _gate = new();
    private readonly Dictionary<(PowerMode?, string), (double Minutes, double WattMinutes)> _sums = [];

    public void Add(BatteryInterval i) => Add(i.Mode, i.Profile, i.Average.Span.TotalMinutes, i.Average.Watts);

    private void Add(PowerMode? mode, string profile, double minutes, double watts)
    {
        if (minutes <= 0 || watts < 0) return;
        lock (_gate)
        {
            var (m, wm) = _sums.GetValueOrDefault((mode, profile));
            _sums[(mode, profile)] = (m + minutes, wm + watts * minutes);
        }
    }

    /// <summary>Most used first.</summary>
    public IReadOnlyList<BatteryEstimate> All
    {
        get
        {
            lock (_gate)
                return _sums.Select(kv => new BatteryEstimate(kv.Key.Item1, kv.Key.Item2, kv.Value.Minutes, kv.Value.WattMinutes / kv.Value.Minutes))
                    .OrderByDescending(e => e.Minutes).ToList();
        }
    }

    /// <summary>Reads every csv in <paramref name="directory"/>; unreadable files and lines are skipped.</summary>
    public static BatteryEstimates Load(string directory, Action<string>? log = null)
    {
        var e = new BatteryEstimates();
        if (!Directory.Exists(directory)) return e;
        int lines = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*.csv"))
        {
            try
            {
                foreach (string line in File.ReadLines(file).Skip(1))
                    if (e.TryAddLine(line)) lines++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"battery: {Path.GetFileName(file)} not read: {ex.Message}");
            }
        }
        if (lines > 0) log?.Invoke($"battery: {lines} intervals read for the estimates");
        return e;
    }

    /// <summary>One <see cref="BatteryCsvLog.Header"/> line: time,minutes,watts,percent,power_mode,fan_profile,brightness.</summary>
    public bool TryAddLine(string line)
    {
        var f = line.Split(',');
        if (f.Length < 6
            || !double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes)
            || !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double watts)
            || f[5].Length == 0)
            return false;
        PowerMode? mode = Enum.TryParse(f[4], out PowerMode m) && Enum.IsDefined(m) ? m : null;
        Add(mode, f[5], minutes, watts);
        return true;
    }
}
