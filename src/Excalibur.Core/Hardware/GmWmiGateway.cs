using System.Diagnostics;
using System.Management;

namespace Excalibur.Core.Hardware;

/// <summary>Sends one firmware command and returns its response; <see cref="GmWmiGateway"/> on the machine, a fake in tests.</summary>
public interface ISmiChannel
{
    SmiCommand Execute(SmiCommand request);
}

/// <summary>
/// Talks to the firmware's ACPI WMI method <c>\_SB.GWMI</c>, exposed to Windows as
/// <c>root\wmi:RW_GMWMI</c>. Writing <c>BufferBytes</c> runs the BIOS handler (WSAA);
/// reading it back returns the response (WQAA). Works without administrator rights.
/// </summary>
public sealed class GmWmiGateway : ISmiChannel
{
    private const string Scope = @"root\wmi";
    private const string ClassName = "RW_GMWMI";
    private readonly object _gate = new();
    private readonly StallGuard _guard = new(StallGuard.DefaultTimeout, m => SlowOps.Report?.Invoke(m));

    /// <summary>True when the WMI class exists on this machine.</summary>
    public bool IsAvailable()
    {
        try { return GetInstance() is not null; }
        catch (ManagementException) { return false; }
    }

    /// <summary>Sends one command and returns the firmware's response block.</summary>
    public SmiCommand Execute(SmiCommand request) => Send(request, skipWhenBusy: false)!.Value;

    /// <summary>
    /// Like <see cref="Execute"/>, but sends nothing and returns null when the EC lock is not free within 2 s: for reads
    /// whose last answer can stand in. Every ACPI 13 since the lock came in (11, 2026-10-23 to 10-26) followed one
    /// process's sensor call stalling 3-4.5 s while the other gave up waiting and sent its own on top of it.
    /// </summary>
    public SmiCommand? TryExecute(SmiCommand request) => Send(request, skipWhenBusy: true);

    private SmiCommand? Send(SmiCommand request, bool skipWhenBusy)
    {
        // The firmware keeps a single shared response buffer, so requests must not interleave.
        lock (_gate)
        {
            // The handler reads the EC through Windows' driver, so a collision stalls this call.
            // Taking the EC lock keeps our other process's raw EC access out meanwhile; when it is
            // not free within 2 s a command goes ahead anyway (power mode and lighting must not be lost).
            // A call that hangs gives the lock back after 5 s (StallGuard); while it hangs nothing waits for the lock.
            string what = $"wmi 0x{request.A1:X4}";
            _guard.ThrowIfStalled(what);
            using var ecLock = EcLock.TryAcquire();
            if (skipWhenBusy && !ecLock.Held) return null;
            return _guard.Run(what, () =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using var instance = GetInstance()
                        ?? throw new InvalidOperationException($"{ClassName} is not present on this system.");
                    instance["BufferBytes"] = request.ToBytes();
                    instance.Put();

                    using var reply = GetInstance()
                        ?? throw new InvalidOperationException($"{ClassName} disappeared after write.");
                    return SmiCommand.FromBytes((byte[])reply["BufferBytes"]);
                }
                finally { SlowOps.Check(what, sw.Elapsed); }
            });
        }
    }

    private static ManagementObject? GetInstance()
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(Scope), new SelectQuery(ClassName));
        foreach (ManagementObject obj in searcher.Get())
            return obj;
        return null;
    }
}
