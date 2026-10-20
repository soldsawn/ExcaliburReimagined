using Excalibur.Core.Fan;

namespace Excalibur.FanService;

/// <summary>
/// Whether the service drives the real EC or the in-memory simulation, and where its files
/// live. The simulation keeps its own directory so it can never overwrite the real settings
/// or be mistaken for the real service by the guardian.
/// </summary>
public sealed record FanRuntime(bool Simulated, FakeFanHardware? Fake, DateTimeOffset StartedAt, string DataDirectory)
{
    public string HeartbeatPath => Path.Combine(DataDirectory, "heartbeat.bin");
    public string SettingsPath => Path.Combine(DataDirectory, "fan.json");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string HistoryDirectory => Path.Combine(DataDirectory, "history");
}

/// <summary>Suspend/resume notifications, from the service control manager or (console) SystemEvents.</summary>
public sealed class PowerSignal
{
    public event Action<bool>? Changed; // true = suspending, false = resumed

    public void Raise(bool suspending) => Changed?.Invoke(suspending);
}
