using System.Runtime.InteropServices;
using System.Text;

namespace Excalibur.Core.Battery;

/// <summary>
/// What the battery reports about itself; changes slowly, so the service reads it at start and once a day.
/// <see cref="Cycles"/> is null when the battery does not count them (the G870's reports 0).
/// </summary>
public sealed record BatteryHealth(int DesignedMWh, int FullMWh, string? Chemistry, int? Cycles, DateTimeOffset ReadAt)
{
    /// <summary>Full charge against the design capacity; a new battery often holds a little more, shown as 100.</summary>
    public int? Percent => DesignedMWh > 0 ? Math.Min(100, (int)Math.Round(FullMWh * 100.0 / DesignedMWh)) : null;
}

/// <summary>Where the battery's charge is going right now.</summary>
public enum BatteryState
{
    OnBattery = 0,
    Charging = 1,
    Full = 2,
    /// <summary>On the wall adapter but not charging and not full (the firmware paused charging).</summary>
    NotCharging = 3,
}

/// <summary>What Windows already knows about the battery (GetSystemPowerStatus): no request to the battery or the EC.</summary>
public readonly record struct BatteryLive(int? Percent, BatteryState State, int? RemainingMinutes)
{
    private const byte FlagCharging = 8, FlagNoBattery = 128, FlagUnknown = 255;

    /// <summary>Null when there is no battery. <paramref name="lifeSeconds"/> is -1 when Windows has no estimate (always on the wall).</summary>
    public static BatteryLive? From(byte acLine, byte flag, byte percent, int lifeSeconds)
    {
        if (flag == FlagUnknown || (flag & FlagNoBattery) != 0) return null;
        int? p = percent <= 100 ? percent : null;
        var state = acLine == 0 ? BatteryState.OnBattery
            : (flag & FlagCharging) != 0 ? BatteryState.Charging
            : p == 100 ? BatteryState.Full
            : BatteryState.NotCharging;
        int? minutes = state == BatteryState.OnBattery && lifeSeconds > 0 ? lifeSeconds / 60 : null;
        return new BatteryLive(p, state, minutes);
    }

    public static BatteryLive? Read() =>
        GetSystemPowerStatus(out var s) ? From(s.ACLineStatus, s.BatteryFlag, s.BatteryLifePercent, s.BatteryLifeTime) : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}

public static class BatteryChemistry
{
    /// <summary>
    /// The battery's chemistry code ("LION", "LiP", "NiMH" ...), packed four characters to a number by WMI
    /// (BatteryStaticData.Chemistry, first character in the low byte); null when it is not readable text.
    /// </summary>
    public static string? Decode(uint raw)
    {
        var bytes = BitConverter.GetBytes(raw);
        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
        int length = Array.IndexOf(bytes, (byte)0) is int end and >= 0 ? end : bytes.Length;
        if (length == 0) return null;
        for (int i = 0; i < length; i++)
            if (bytes[i] is < 0x20 or > 0x7E) return null;
        return Encoding.ASCII.GetString(bytes, 0, length).Trim();
    }
}

/// <summary>Reads the battery's slow-changing facts; null when there is no battery. May take seconds (WMI).</summary>
public interface IBatteryHardware
{
    BatteryHealth? ReadHealth();

    BatteryLive? ReadLive();
}
