using System.Diagnostics;

namespace Excalibur.Core.Hardware;

/// <summary>
/// RAII holder for the global "Access_EC" mutex that EC tools share. Our raw port access and our
/// WMI calls (which read the EC through Windows' driver) both take it, so the service and the
/// guardian never talk to the EC at the same moment.
/// </summary>
internal readonly struct EcLock : IDisposable
{
    private const string MutexName = @"Global\Access_EC"; // -> \BaseNamedObjects\Access_EC
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    private readonly Mutex? _mutex;

    private EcLock(Mutex mutex) => _mutex = mutex;

    /// <summary>False for the empty holder <see cref="TryAcquire"/> returns when the lock was not free.</summary>
    public bool Held => _mutex is not null;

    /// <summary>Takes the lock or throws when it is not free within 2 s (raw EC access must not go unlocked).</summary>
    public static EcLock Acquire()
        => TryAcquire() is { _mutex: not null } held ? held
            : throw new TimeoutException("Could not acquire the system EC lock (Access_EC) within 2s.");

    /// <summary>Takes the lock, or returns an empty holder when it is not free within 2 s.</summary>
    public static EcLock TryAcquire()
    {
        var mutex = new Mutex(false, MutexName);
        bool owned;
        var sw = Stopwatch.StartNew();
        try { owned = mutex.WaitOne(Wait); }
        catch (AbandonedMutexException) { owned = true; } // prior owner crashed; we hold it now
        SlowOps.Check("ec lock wait", sw.Elapsed);
        if (owned) return new EcLock(mutex);
        mutex.Dispose();
        return default;
    }

    public void Dispose()
    {
        if (_mutex is null) return;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
