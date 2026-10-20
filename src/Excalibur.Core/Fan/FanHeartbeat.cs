using System.Buffers.Binary;

namespace Excalibur.Core.Fan;

public readonly record struct HeartbeatRecord(long Sequence, DateTimeOffset At, bool HoldsFans, int ProcessId);

/// <summary>
/// The fan service writes this small file every tick; the guardian reads it. If it goes
/// stale while <see cref="HeartbeatRecord.HoldsFans"/> is set, the service is dead with a
/// fan target still in the EC (which the EC never undoes), and the guardian hands the fans
/// back. Layout: u64 sequence, i64 unix ms, u32 holdsFans, i32 pid (24 bytes, little-endian).
/// </summary>
public static class FanHeartbeat
{
    private const int Size = 24;

    public static string DefaultPath { get; } = Path.Combine(FanPaths.DataDirectory, "heartbeat.bin");

    public static void Write(string path, HeartbeatRecord r)
    {
        Span<byte> b = stackalloc byte[Size];
        BinaryPrimitives.WriteInt64LittleEndian(b, r.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(b[8..], r.At.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteUInt32LittleEndian(b[16..], r.HoldsFans ? 1u : 0u);
        BinaryPrimitives.WriteInt32LittleEndian(b[20..], r.ProcessId);
        // Overwrite in place: a 24-byte write is one sector, so a reader never sees half of it.
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
            if (fs.Read(b) != Size) return null;
            return new HeartbeatRecord(
                BinaryPrimitives.ReadInt64LittleEndian(b),
                DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(b[8..])),
                BinaryPrimitives.ReadUInt32LittleEndian(b[16..]) != 0,
                BinaryPrimitives.ReadInt32LittleEndian(b[20..]));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

public static class FanPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ExcaliburReimagined");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "fan.json");
}
