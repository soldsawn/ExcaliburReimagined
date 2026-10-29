using System.Diagnostics;

namespace Excalibur.Core.Hardware;

/// <summary>
/// Read access to the ACPI Embedded Controller RAM (0x00-0xFF) over PawnIO's
/// LpcACPIEC module, which permits byte I/O only on the two standard ACPI EC
/// ports (0x62 data, 0x66 command/status). We implement the documented ACPI EC
/// read protocol on top of that.
///
/// Reading is the normal path. <see cref="WriteByte"/> exists only for the gated fan
/// experiments (see docs/06): it must be called behind explicit user consent, with
/// an address whitelist and automatic restore of the original value.
///
/// Reads are serialized against other EC tools through the global "Access_EC" mutex.
/// Windows' own ACPI EC driver does NOT take it, so every transaction can still collide
/// with Windows reading the battery from the same EC; callers must keep EC traffic low.
/// </summary>
public sealed class EmbeddedController : IDisposable
{
    private const ushort DataPort = 0x62;
    private const ushort CommandPort = 0x66;
    private const byte StatusObf = 0x01; // output buffer full (data ready to read)
    private const byte StatusIbf = 0x02; // input buffer full (controller still busy)
    private const byte CmdRead = 0x80;   // RD_EC
    private const byte CmdWrite = 0x81;  // WR_EC

    // A healthy EC completes a read in well under a millisecond; this ceiling only
    // bounds how long we wait on a register that never responds.
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromMilliseconds(100);
    // A register that does not respond leaves the controller half-way through a
    // transaction: the EC may still deliver its byte a little later. Without recovery
    // that late byte desyncs every read that follows (the whole sweep then times out).
    // So on a timeout we settle the bus and retry the read a few times.
    private const int MaxReadRetries = 3;
    // A byte left in the output buffer this long is nobody's pending reply (Windows' transactions
    // take milliseconds); it is a stale one of ours and would block every operation, so drain it.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(2);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _busySince;

    private readonly PawnIoSession _session;

    public EmbeddedController(PawnIoSession session) => _session = session;

    public static EmbeddedController Open(string lpcAcpiEcBlobPath)
        => new(PawnIoSession.Load(File.ReadAllBytes(lpcAcpiEcBlobPath)));

    /// <summary>
    /// Reads one EC register under the EC mutex. Waits for an idle bus before the transaction and,
    /// if the handshake times out, settles the bus and retries up to
    /// <see cref="MaxReadRetries"/> times so one unresponsive register cannot desync
    /// the reads that follow it.
    /// </summary>
    public byte ReadByte(byte address, int retries = MaxReadRetries)
    {
        using var ecLock = EcLock.Acquire();
        var sw = Stopwatch.StartNew();
        try
        {
            TimeoutException? last = null;
            for (int attempt = 0; attempt <= retries; attempt++)
            {
                try
                {
                    Resync();
                    return ReadByteLocked(address);
                }
                catch (TimeoutException ex) when (ex is not EcBusyException)
                {
                    last = ex;
                    SettleAfterTimeout();
                }
            }
            throw last!;
        }
        finally { SlowOps.Check($"ec read 0x{address:X2}", sw.Elapsed); }
    }

    /// <summary>
    /// Reads a register until two consecutive reads agree. Windows' own ACPI EC driver
    /// shares these ports without taking our mutex, so a stray reply (e.g. its 0x90
    /// burst-mode ack) can occasionally arrive in place of ours; a single read is not
    /// trustworthy. A timeout resets the comparison, since it may have desynced the bus.
    /// </summary>
    public byte ReadStable(byte address, int attempts = 8, int retries = MaxReadRetries)
    {
        byte? previous = null;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                byte value = ReadByte(address, retries);
                if (previous == value) return value;
                previous = value;
            }
            catch (TimeoutException ex) when (ex is not EcBusyException) { previous = null; }
        }
        throw new TimeoutException($"EC register 0x{address:X2} never gave two matching reads.");
    }

    /// <summary>
    /// Reads every register in <paramref name="addresses"/>, each as an independent
    /// locked+resynced transaction so one unresponsive register never corrupts the
    /// next. Registers that time out are recorded in <paramref name="unreadable"/>
    /// and left as 0x00.
    /// </summary>
    public byte[] Dump(IEnumerable<byte> addresses, out IReadOnlyList<byte> unreadable)
    {
        var map = new byte[256];
        var failed = new List<byte>();
        foreach (byte addr in addresses)
        {
            try { map[addr] = ReadStable(addr, attempts: 4, retries: 1); }
            catch (TimeoutException) { failed.Add(addr); }
        }
        unreadable = failed;
        return map;
    }

    /// <summary>
    /// Writes one EC register (ACPI WR_EC, command 0x81) under the EC mutex. Single
    /// attempt, no retry: if the handshake times out we let the bus settle and stop, rather than
    /// risk re-sending bytes into a half-finished transaction; the caller decides what follows.
    /// Callers must restrict which addresses they pass; this method does not.
    /// With <paramref name="lockOptional"/> it writes even when the EC lock is not free within 2 s: for handing the fans
    /// back, which must not wait on a holder that hangs (a WMI call held it 42 min, 2026-10-29).
    /// </summary>
    public void WriteByte(byte address, byte value, bool lockOptional = false)
    {
        using var ecLock = lockOptional ? EcLock.TryAcquire() : EcLock.Acquire();
        string what = ecLock.Held ? $"ec write 0x{address:X2}" : $"ec write 0x{address:X2} unlocked";
        var sw = Stopwatch.StartNew();
        try
        {
            Resync();
            WaitStatus(StatusIbf, expectSet: false);
            WritePort(CommandPort, CmdWrite);
            WaitStatus(StatusIbf, expectSet: false);
            WritePort(DataPort, address);
            WaitStatus(StatusIbf, expectSet: false);
            WritePort(DataPort, value);
            WaitStatus(StatusIbf, expectSet: false);
        }
        catch (TimeoutException ex) when (ex is not EcBusyException)
        {
            // Leave the bus idle for whoever goes next (us retrying, or Windows).
            SettleAfterTimeout();
            throw;
        }
        finally { SlowOps.Check(what, sw.Elapsed); }
    }

    /// <summary>Raw EC status byte (port 0x66): bit0 = OBF, bit1 = IBF. Diagnostic only.</summary>
    public byte ReadStatus()
    {
        using var ecLock = EcLock.Acquire();
        return ReadPort(CommandPort);
    }

    /// <summary>
    /// One read transaction with a status trace after every step, for diagnosing
    /// failed handshakes. Does not retry or recover. Read-only (command 0x80).
    /// </summary>
    public string TraceRead(byte address)
    {
        using var ecLock = EcLock.Acquire();
        var sb = new System.Text.StringBuilder();
        string St() => $"st=0x{ReadPort(CommandPort):X2}";
        bool Wait(byte flag, bool set, string label)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < StatusTimeout)
            {
                if (((ReadPort(CommandPort) & flag) != 0) == set) { sb.Append($"{label}:ok({sw.Elapsed.TotalMilliseconds:F2}ms) "); return true; }
            }
            sb.Append($"{label}:TIMEOUT {St()} ");
            return false;
        }

        sb.Append($"start {St()} | ");
        if (!Wait(StatusIbf, false, "ibf0-pre")) return sb.ToString();
        WritePort(CommandPort, CmdRead);
        sb.Append($"cmd-sent {St()} | ");
        if (!Wait(StatusIbf, false, "ibf0-cmd")) return sb.ToString();
        WritePort(DataPort, address);
        sb.Append($"addr-sent {St()} | ");
        if (!Wait(StatusObf, true, "obf1")) return sb.ToString();
        sb.Append($"value=0x{ReadPort(DataPort):X2}");
        return sb.ToString();
    }

    /// <summary>Reads the full 0x00-0xFF window.</summary>
    public byte[] Dump(out IReadOnlyList<byte> unreadable)
    {
        var all = new byte[256];
        for (int i = 0; i < 256; i++) all[i] = (byte)i;
        return Dump(all, out unreadable);
    }

    private byte ReadByteLocked(byte address)
    {
        WaitStatus(StatusIbf, expectSet: false); // controller ready for a command
        WritePort(CommandPort, CmdRead);
        WaitStatus(StatusIbf, expectSet: false); // command byte consumed
        WritePort(DataPort, address);
        WaitStatus(StatusObf, expectSet: true);  // result byte available
        return ReadPort(DataPort);
    }

    // Wait for the controller to go idle before a transaction. A pending output byte is
    // most likely the reply Windows' own EC driver is waiting for (e.g. a battery read);
    // taking it makes Windows time out (ACPI event 13) or read garbage, and a garbage
    // battery charge shuts the machine down as "critical battery". So when the bus is not
    // idle within StatusTimeout, skip this operation (EcBusyException; the caller tries again
    // later) and only drain a byte that has been sitting there for StaleAfter.
    private void Resync()
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < StatusTimeout)
        {
            if ((ReadPort(CommandPort) & (StatusObf | StatusIbf)) == 0)
            {
                _busySince = null;
                return;
            }
        }
        var now = _clock.Elapsed;
        _busySince ??= now;
        if (now - _busySince < StaleAfter)
            throw new EcBusyException("The EC bus is busy (another driver's transaction); the operation was skipped.");
        _busySince = null;
        for (int i = 0; i < 32 && (ReadPort(CommandPort) & StatusObf) != 0; i++)
            _ = ReadPort(DataPort);
    }

    // After a handshake times out the EC may still be about to deliver the byte we
    // asked for. Give the bus time to go idle; Resync drains the byte if nobody takes it.
    private void SettleAfterTimeout()
    {
        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(2);
            if ((ReadPort(CommandPort) & (StatusObf | StatusIbf)) == 0) return; // idle and empty
        }
    }

    private byte ReadPort(ushort port)
        => (byte)_session.Execute("ioctl_pio_read", [port], 1)[0];

    private void WritePort(ushort port, byte value)
        => _session.Execute("ioctl_pio_write", [port, value], 0);

    private void WaitStatus(byte flag, bool expectSet)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < StatusTimeout)
        {
            bool isSet = (ReadPort(CommandPort) & flag) != 0;
            if (isSet == expectSet) return;
            // No Sleep here: Thread.Sleep(1) really sleeps ~15 ms on Windows, long enough
            // for the OS's own ACPI EC driver to drain the reply byte before we see OBF.
        }
        throw new TimeoutException(
            $"EC status flag 0x{flag:X2} did not become {(expectSet ? "set" : "clear")} within {StatusTimeout.TotalMilliseconds:F0} ms.");
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>The EC bus was not idle before a transaction, so nothing was sent; try again later.</summary>
public sealed class EcBusyException(string message) : TimeoutException(message);
