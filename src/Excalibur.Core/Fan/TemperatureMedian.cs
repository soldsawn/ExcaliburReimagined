namespace Excalibur.Core.Fan;

/// <summary>
/// The median of the readings in the last <paramref name="window"/>. The CPU's own sensor jumps 5-30 C from one
/// reading to the next (2026-10-23: 5.6 C per second on average at light load), which made the CPU graph and the
/// curve input zigzag; the service samples it several times a second and uses this median. On that day's 4 Hz
/// recording a 2 s median halved the idle jitter and still showed a real climb to 92 C within 1.7 s (a 2 s mean:
/// 2.0 s and more jitter). Thread-safe: a timer adds, the fan loop reads. Time is monotonic (Stopwatch).
/// </summary>
public sealed class TemperatureMedian(TimeSpan window)
{
    private readonly Queue<(TimeSpan At, int C)> _readings = new();
    private readonly object _gate = new();

    public void Add(int celsius, TimeSpan now)
    {
        lock (_gate)
        {
            _readings.Enqueue((now, celsius));
            Trim(now);
        }
    }

    /// <summary>Median of the readings in the window (rounded half up for an even count); null when there are none.</summary>
    public int? Value(TimeSpan now)
    {
        lock (_gate)
        {
            Trim(now);
            if (_readings.Count == 0) return null;
            int[] sorted = _readings.Select(r => r.C).Order().ToArray();
            int mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid] + 1) / 2;
        }
    }

    private void Trim(TimeSpan now)
    {
        while (_readings.Count > 0 && now - _readings.Peek().At > window) _readings.Dequeue();
    }
}
