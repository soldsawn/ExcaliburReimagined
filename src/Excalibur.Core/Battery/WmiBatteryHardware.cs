using System.Diagnostics;
using System.Management;
using Excalibur.Core.Hardware;

namespace Excalibur.Core.Battery;

/// <summary>
/// The battery through Windows' battery driver (root\wmi BatteryStaticData, BatteryFullChargedCapacity, BatteryCycleCount).
/// The static data needs administrator rights ("Generic failure" otherwise), so only the service reads it. The driver asks
/// the firmware (ACPI _BIX, which reads the EC) when its copy is stale: the read is timed like any EC call.
/// </summary>
public sealed class WmiBatteryHardware : IBatteryHardware
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public BatteryHealth? ReadHealth()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var stat = First("BatteryStaticData", "DesignedCapacity", "Chemistry");
            var full = First("BatteryFullChargedCapacity", "FullChargedCapacity");
            if (stat?[0] is not { } designed || full?[0] is not { } fullMWh) return null;
            string? chemistry = stat[1] is { } c ? BatteryChemistry.Decode(Convert.ToUInt32(c)) : null;
            int? cycles = null;
            try
            {
                if (First("BatteryCycleCount", "CycleCount")?[0] is { } n && Convert.ToInt32(n) is int count and > 0) cycles = count;
            }
            catch (ManagementException) { } // not every battery has it
            return new BatteryHealth(Convert.ToInt32(designed), Convert.ToInt32(fullMWh), chemistry, cycles, DateTimeOffset.Now);
        }
        finally
        {
            SlowOps.Check("battery health", watch.Elapsed);
        }
    }

    public BatteryLive? ReadLive() => BatteryLive.Read();

    /// <summary>The given properties of the first instance (the first battery); null when there is none.</summary>
    private static object?[]? First(string wmiClass, params string[] properties)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\wmi"),
            new ObjectQuery($"SELECT {string.Join(", ", properties)} FROM {wmiClass}"),
            new System.Management.EnumerationOptions { Timeout = Timeout, ReturnImmediately = false });
        using var results = searcher.Get();
        foreach (ManagementBaseObject o in results)
            using (o)
                return properties.Select(p => o[p]).ToArray();
        return null;
    }
}

/// <summary>The G870's battery as read on 2026-10-24, for the --simulate service.</summary>
public sealed class SimulatedBatteryHardware : IBatteryHardware
{
    public BatteryHealth? ReadHealth() => new(61500, 62800, "LION", null, DateTimeOffset.Now);

    public BatteryLive? ReadLive() => new BatteryLive(64, BatteryState.Charging, null);
}
