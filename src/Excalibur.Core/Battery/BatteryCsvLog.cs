using System.Globalization;

namespace Excalibur.Core.Battery;

/// <summary>
/// The kept battery intervals, one csv file a day (battery\YYYYMMDD.csv): end time, minutes, average watts, battery percent,
/// power mode, fan profile, screen brightness. Files older than <see cref="KeepDays"/> are deleted when a new day starts.
/// </summary>
public sealed class BatteryCsvLog(string directory, Action<string>? log = null)
{
    public const int KeepDays = 30;
    public const string Header = "time,minutes,watts,percent,power_mode,fan_profile,brightness";
    private DateOnly? _prunedOn;

    public void Append(BatteryInterval interval)
    {
        var (average, percent, mode, profile, brightness) = interval;
        try
        {
            Directory.CreateDirectory(directory);
            var day = DateOnly.FromDateTime(average.At.LocalDateTime);
            if (_prunedOn != day) Prune(day);
            string path = Path.Combine(directory, $"{day:yyyyMMdd}.csv");
            var c = CultureInfo.InvariantCulture;
            string line = string.Join(",",
                average.At.ToString("yyyy-MM-dd HH:mm:ss", c),
                average.Span.TotalMinutes.ToString("0.0", c),
                average.Watts.ToString("0.00", c),
                percent?.ToString(c) ?? "",
                mode?.ToString() ?? "",
                profile.Replace(",", " "),
                brightness?.ToString(c) ?? "");
            File.AppendAllText(path, (File.Exists(path) ? "" : Header + Environment.NewLine) + line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"battery: csv not written: {ex.Message}");
        }
    }

    private void Prune(DateOnly today)
    {
        _prunedOn = today;
        foreach (string file in Directory.EnumerateFiles(directory, "*.csv"))
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyyMMdd", out var d) && d < today.AddDays(-KeepDays))
                File.Delete(file);
    }
}
