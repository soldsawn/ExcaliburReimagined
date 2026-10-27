namespace Excalibur.Core.Power;

/// <summary>
/// The user's GPU power target in watts. The laptop's driver refuses a power limit (nvidia-smi -pl: "not supported in
/// current scope", as administrator too, 2026-10-27) but takes a graphics clock lock: 1500 MHz took a game from 100 W
/// to 40 W and the GPU from 82 to 62 C. <see cref="GpuLimiter"/> moves that lock to hold the target.
/// </summary>
public static class GpuLimit
{
    // The top is wide for other laptops' cards (up to 175 W); the page offers only up to the limit the driver enforces now.
    public const int MinWatts = 30, MaxWatts = 175, Step = 5;

    public static IReadOnlyList<string> Validate(int watts) =>
        watts is < MinWatts or > MaxWatts || watts % Step != 0 ? [$"GPU limit must be {MinWatts}-{MaxWatts} W in steps of {Step}"] : [];
}

/// <summary>The GPU's power and graphics clock, and its clock lock (NVML); reads are null when the GPU cannot tell (asleep).</summary>
public interface IGpuClocks
{
    int? PowerWatts();
    int? GraphicsClockMhz();
    int? MaxClockMhz();
    /// <summary>Locks the graphics clock at or below <paramref name="maxMhz"/>; throws <see cref="PowerException"/> when refused.</summary>
    void Lock(int maxMhz);
    void Unlock();
}

/// <summary>
/// Holds the GPU near a watt target by moving the clock lock, once per power poll (2 s). Power falls faster than the
/// clock (voltage drops with it): the step scales the clock by the square root of target/power, which put 100 W at
/// 2450 MHz on ~1550 MHz for 40 W, the measured 1500 MHz. Above the target the lock comes down; below it, and only while
/// the GPU runs at the lock (a light load is not held back by it), the lock goes up by at most <see cref="MaxRaiseMhz"/>
/// and is removed once it passes the GPU's top clock.
/// </summary>
public sealed class GpuLimiter(IGpuClocks clocks)
{
    public const int FloorMhz = 600, StepMhz = 15, MaxRaiseMhz = 150, MinGpuMhz = 210;
    /// <summary>Within target - <see cref="Below"/> .. target + <see cref="Above"/> nothing changes.</summary>
    public const int Above = 2, Below = 5;
    // A clock this far over the lock means the driver dropped it (the GPU slept or was reset).
    private const int LostMhz = 50;

    private bool _cleared;
    private int? _maxMhz;

    /// <summary>The clock lock in force, null when the GPU runs free.</summary>
    public int? LockMhz { get; private set; }

    /// <param name="targetWatts">Null removes the lock.</param>
    public void Apply(int? targetWatts)
    {
        // A lock left by a service that died stays until Windows restarts: clear it once.
        if (!_cleared)
        {
            _cleared = true;
            Unlock();
        }
        if (targetWatts is not { } target)
        {
            if (LockMhz is not null) Unlock();
            return;
        }
        if (clocks.PowerWatts() is not { } watts || clocks.GraphicsClockMhz() is not { } clock || watts <= 0) return;
        int max = _maxMhz ??= clocks.MaxClockMhz() ?? 3105;
        if (LockMhz is { } held && clock > held + LostMhz) LockMhz = null;

        if (watts > target + Above)
        {
            int from = LockMhz ?? clock;
            int next = Math.Max(FloorMhz, RoundDown(from * Math.Sqrt((double)target / watts)));
            if (LockMhz is null || next < LockMhz) Lock(next);
        }
        else if (LockMhz is { } current && watts < target - Below && clock >= current - 2 * StepMhz)
        {
            int next = Math.Min(current + MaxRaiseMhz, RoundDown(current * Math.Sqrt((double)target / watts)));
            if (next >= max) Unlock();
            else if (next > current) Lock(next);
        }
    }

    private static int RoundDown(double mhz) => (int)(mhz / StepMhz) * StepMhz;

    private void Lock(int mhz)
    {
        clocks.Lock(mhz);
        LockMhz = mhz;
    }

    private void Unlock()
    {
        clocks.Unlock();
        LockMhz = null;
    }
}
