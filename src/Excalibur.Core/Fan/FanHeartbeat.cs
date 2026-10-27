using System.Buffers.Binary;

namespace Excalibur.Core.Fan;

/// <param name="Sensors">What the service read in the tick that wrote it (null when that tick read nothing): the guardian
/// uses these instead of asking the firmware itself.</param>
/// <param name="WmiFans">The fans are written through the BIOS (WMI SET 0x0205), so that is how they are handed back.</param>
/// <param name="CpuTarget">The targets of the tick that read <see cref="Sensors"/> (null with them): a fan still spinning up
/// to a high target is not the service misbehaving.</param>
public readonly record struct HeartbeatRecord(long Sequence, DateTimeOffset At, bool HoldsFans, int ProcessId, FanSensors? Sensors = null,
    bool WmiFans = false, FanTarget? CpuTarget = null, FanTarget? GpuTarget = null);

/// <summary>
/// The fan service writes this small file every tick; the guardian reads it. If it goes
/// stale while <see cref="HeartbeatRecord.HoldsFans"/> is set, the service is dead with a
/// fan target still in the EC (which the EC never undoes), and the guardian hands the fans
/// back. Layout: u64 sequence, i64 unix ms, u32 flags (bit 0 holdsFans, bit 1 WMI fans; an older guardian reads any
/// non-zero value as holding), i32 pid, then i32 CPU C, GPU C, CPU %, GPU % (CPU C = -1:
/// no sensors), then i32 CPU and GPU target as the EC byte (-1: none); 48 bytes, little-endian. A 24-byte file (written
/// by an older service) reads without sensors, a 40-byte one without targets; an older guardian reads the first 40.
/// </summary>
public static class FanHeartbeat
{
    private const int Size = 48, NoTargetsSize = 40, OldSize = 24;

    public static string DefaultPath { get; } = Path.Combine(FanPaths.DataDirectory, "heartbeat.bin");

    public static void Write(string path, HeartbeatRecord r)
    {
        Span<byte> b = stackalloc byte[Size];
        BinaryPrimitives.WriteInt64LittleEndian(b, r.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(b[8..], r.At.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteUInt32LittleEndian(b[16..], (r.HoldsFans ? 1u : 0u) | (r.WmiFans ? 2u : 0u));
        BinaryPrimitives.WriteInt32LittleEndian(b[20..], r.ProcessId);
        BinaryPrimitives.WriteInt32LittleEndian(b[24..], r.Sensors?.CpuTempC ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(b[28..], r.Sensors?.GpuTempC ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(b[32..], r.Sensors?.CpuAppliedPercent ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(b[36..], r.Sensors?.GpuAppliedPercent ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(b[40..], r.CpuTarget?.Value ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(b[44..], r.GpuTarget?.Value ?? -1);
        // Overwrite in place: a 48-byte write is one sector, so a reader never sees half of it.
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        fs.Write(b);
        fs.SetLength(Size);
    }

    /// <summary>Null when the file is missing or malformed.</summary>
    public static HeartbeatRecord? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> b = stackalloc byte[Size];
            int read = fs.Read(b);
            if (read != Size && read != NoTargetsSize && read != OldSize) return null;
            int cpuC = read >= NoTargetsSize ? BinaryPrimitives.ReadInt32LittleEndian(b[24..]) : -1;
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(b[16..]);
            return new HeartbeatRecord(
                BinaryPrimitives.ReadInt64LittleEndian(b),
                DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(b[8..])),
                (flags & 1) != 0,
                BinaryPrimitives.ReadInt32LittleEndian(b[20..]),
                cpuC < 0 ? null : new FanSensors(cpuC, BinaryPrimitives.ReadInt32LittleEndian(b[28..]),
                    BinaryPrimitives.ReadInt32LittleEndian(b[32..]), BinaryPrimitives.ReadInt32LittleEndian(b[36..])),
                (flags & 2) != 0,
                read == Size ? Target(b[40..]) : null,
                read == Size ? Target(b[44..]) : null);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static FanTarget? Target(ReadOnlySpan<byte> b) =>
        BinaryPrimitives.ReadInt32LittleEndian(b) is var v and >= 0 and <= 0xFF ? FanTarget.FromRaw((byte)v) : null;
}

public static class FanPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ExcaliburReimagined");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "fan.json");
}
