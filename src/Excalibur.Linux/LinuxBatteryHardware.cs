using System.Globalization;
using Excalibur.Core.Battery;

namespace Excalibur.Linux;

/// <summary>
/// The battery on Linux (L2) through /sys/class/power_supply/BAT* and /sys/class/backlight/*:
/// health (design/full capacity, technology, cycle count), live state (percent, charging/discharging, time left from
/// energy_now and power_now), and the meter readings for <see cref="BatteryRecorder"/>.
/// </summary>
internal sealed class LinuxBatteryHardware(
    string powerSupplyRoot = "/sys/class/power_supply",
    string backlightRoot = "/sys/class/backlight") : IBatteryHardware, IBatteryMeter
{
    public BatteryHealth? ReadHealth()
    {
        if (FindBatteryDir() is not { } dir) return null;
        int? designed = ReadEnergyMWh(dir, "energy_full_design", "charge_full_design");
        int? full = ReadEnergyMWh(dir, "energy_full", "charge_full");
        if (designed is not { } d || d <= 0 || full is not { } f || f <= 0) return null;
        string? tech = ReadTrim(Path.Combine(dir, "technology"));
        int? cycles = ReadLong(Path.Combine(dir, "cycle_count")) is { } c and > 0 ? (int)c : null;
        return new BatteryHealth(d, f, tech, cycles, DateTimeOffset.Now);
    }

    public BatteryLive? ReadLive()
    {
        if (FindBatteryDir() is not { } dir) return null;
        int? percent = ReadLong(Path.Combine(dir, "capacity")) is { } cap and >= 0 and <= 100 ? (int)cap : null;
        string? status = ReadTrim(Path.Combine(dir, "status"));
        var state = status?.ToLowerInvariant() switch
        {
            "discharging" => BatteryState.OnBattery,
            "charging" => BatteryState.Charging,
            "full" => BatteryState.Full,
            "not charging" => percent == 100 ? BatteryState.Full : BatteryState.NotCharging,
            _ => MainsOnline() == false ? BatteryState.OnBattery : BatteryState.NotCharging,
        };
        int? remainingMins = null;
        if (state == BatteryState.OnBattery)
        {
            long? energyNowUw = ReadEnergyUWh(dir, "energy_now", "charge_now");
            long? powerNowUw = ReadPowerUw(dir);
            if (energyNowUw is > 0 && powerNowUw is > 100_000) // > 0.1 W
                remainingMins = (int)Math.Round(energyNowUw.Value * 60.0 / powerNowUw.Value);
        }
        return new BatteryLive(percent, state, remainingMins);
    }

    public BatteryReading? Read()
    {
        if (FindBatteryDir() is not { } dir) return null;
        if (ReadEnergyMWh(dir, "energy_now", "charge_now") is not { } remainingMWh || remainingMWh <= 0)
            return null;
        return new BatteryReading(remainingMWh, ReadBrightnessPercent());
    }

    /// <summary>Instantaneous battery discharge/charge power in watts, or null when unavailable.</summary>
    public double? ReadWattsNow()
    {
        if (FindBatteryDir() is not { } dir) return null;
        return ReadPowerUw(dir) is { } uw and > 0 ? Math.Round(uw / 1_000_000.0, 1) : null;
    }

    private string? FindBatteryDir()
    {
        try
        {
            if (!Directory.Exists(powerSupplyRoot)) return null;
            foreach (var dir in Directory.GetDirectories(powerSupplyRoot))
            {
                if (string.Equals(ReadTrim(Path.Combine(dir, "type")), "Battery", StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private bool? MainsOnline()
    {
        try
        {
            if (!Directory.Exists(powerSupplyRoot)) return null;
            foreach (var dir in Directory.GetDirectories(powerSupplyRoot))
            {
                if (string.Equals(ReadTrim(Path.Combine(dir, "type")), "Mains", StringComparison.OrdinalIgnoreCase)
                    && ReadTrim(Path.Combine(dir, "online")) is { } online)
                    return online == "1";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static int? ReadEnergyMWh(string dir, string energyFile, string chargeFile) =>
        ReadEnergyUWh(dir, energyFile, chargeFile) is { } uwh ? (int)Math.Round(uwh / 1000.0) : null;

    private static long? ReadEnergyUWh(string dir, string energyFile, string chargeFile)
    {
        if (ReadLong(Path.Combine(dir, energyFile)) is { } uwh and > 0) return uwh;
        if (ReadLong(Path.Combine(dir, chargeFile)) is { } uah and > 0)
        {
            long uV = ReadLong(Path.Combine(dir, "voltage_min_design"))
                      ?? ReadLong(Path.Combine(dir, "voltage_now"))
                      ?? 11_400_000;
            return uah * uV / 1_000_000;
        }
        return null;
    }

    private static long? ReadPowerUw(string dir)
    {
        if (ReadLong(Path.Combine(dir, "power_now")) is { } uw and > 0) return uw;
        if (ReadLong(Path.Combine(dir, "current_now")) is { } ua and > 0)
        {
            long uV = ReadLong(Path.Combine(dir, "voltage_now"))
                      ?? ReadLong(Path.Combine(dir, "voltage_min_design"))
                      ?? 11_400_000;
            return ua * uV / 1_000_000;
        }
        return null;
    }

    private int? ReadBrightnessPercent()
    {
        try
        {
            if (!Directory.Exists(backlightRoot)) return null;
            foreach (var dir in Directory.GetDirectories(backlightRoot))
            {
                if (ReadLong(Path.Combine(dir, "brightness")) is { } cur
                    && ReadLong(Path.Combine(dir, "max_brightness")) is { } max and > 0)
                    return Math.Clamp((int)Math.Round(cur * 100.0 / max), 0, 100);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static long? ReadLong(string path) =>
        ReadTrim(path) is { } s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;

    private static string? ReadTrim(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
