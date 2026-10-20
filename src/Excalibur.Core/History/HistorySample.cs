using System.Buffers.Binary;
using Excalibur.Core.Fan;

namespace Excalibur.Core.History;

/// <summary>
/// One second of fan history: both chips' temperature, RPM, target and applied percent, why each
/// fan runs as it does, and the loop state. Stored as <see cref="Size"/> bytes (docs/09 A2):
/// <code>
///  0 uint32  seconds since midnight UTC of the file's day
///  4 byte    CPU °C            5 byte  GPU °C
///  6 uint16  CPU rpm           8 uint16 GPU rpm
/// 10 byte    CPU target %      11 byte GPU target %   (255 = EC automatic)
/// 12 byte    CPU applied %     13 byte GPU applied %
/// 14 byte    CPU reason (low 4 bits) | GPU reason (high 4 bits)
/// 15 byte    mode (bits 0-1) | boost (bit 2) | holds fans (bit 3)
/// 16 byte    profile (<see cref="ProfileCode"/>)
/// 17 byte    reserved (0)
/// </code>
/// </summary>
public readonly record struct HistorySample(
    DateTime AtUtc,
    int CpuTempC,
    int GpuTempC,
    int CpuRpm,
    int GpuRpm,
    int? CpuTarget,
    int? GpuTarget,
    int CpuApplied,
    int GpuApplied,
    FanReason CpuReason,
    FanReason GpuReason,
    FanLoopMode Mode,
    bool Boost,
    bool HoldsFans,
    byte Profile)
{
    public const int Size = 18;
    private const byte AutoTarget = 255;

    /// <summary>Null when the loop has no reading yet (nothing worth recording).</summary>
    public static HistorySample? From(FanStatus s, DateTime atUtc)
    {
        if (s.Snapshot is not { } snap) return null;
        return new HistorySample(
            new DateTime(atUtc.Ticks - atUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc),
            snap.CpuTempC, snap.GpuTempC, snap.CpuRpm, snap.GpuRpm,
            Percent(snap.CpuTarget), Percent(snap.GpuTarget), snap.CpuAppliedPercent, snap.GpuAppliedPercent,
            s.CpuReason, s.GpuReason, s.Mode, snap.CoolingBoost, s.HoldsFans, ProfileCode.Of(s.Profile));
    }

    public void Write(Span<byte> b)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)(AtUtc - AtUtc.Date).TotalSeconds);
        b[4] = Byte(CpuTempC);
        b[5] = Byte(GpuTempC);
        BinaryPrimitives.WriteUInt16LittleEndian(b[6..], (ushort)Math.Clamp(CpuRpm, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(b[8..], (ushort)Math.Clamp(GpuRpm, 0, ushort.MaxValue));
        b[10] = CpuTarget is { } ct ? Byte(ct) : AutoTarget;
        b[11] = GpuTarget is { } gt ? Byte(gt) : AutoTarget;
        b[12] = Byte(CpuApplied);
        b[13] = Byte(GpuApplied);
        b[14] = (byte)(((byte)CpuReason & 0x0F) | (((byte)GpuReason & 0x0F) << 4));
        b[15] = (byte)(((int)Mode & 0x03) | (Boost ? 0x04 : 0) | (HoldsFans ? 0x08 : 0));
        b[16] = Profile;
        b[17] = 0;
    }

    /// <param name="dayUtc">Midnight UTC of the file the record came from.</param>
    public static HistorySample Read(ReadOnlySpan<byte> b, DateTime dayUtc) => new(
        dayUtc.AddSeconds(BinaryPrimitives.ReadUInt32LittleEndian(b)),
        b[4], b[5],
        BinaryPrimitives.ReadUInt16LittleEndian(b[6..]), BinaryPrimitives.ReadUInt16LittleEndian(b[8..]),
        b[10] == AutoTarget ? null : b[10], b[11] == AutoTarget ? null : b[11],
        b[12], b[13],
        (FanReason)(b[14] & 0x0F), (FanReason)(b[14] >> 4),
        (FanLoopMode)(b[15] & 0x03), (b[15] & 0x04) != 0, (b[15] & 0x08) != 0,
        b[16]);

    private static int? Percent(FanTarget t) => t.IsAuto ? null : t.Percent;

    private static byte Byte(int v) => (byte)Math.Clamp(v, 0, 254);
}

/// <summary>Profiles as one byte in the history (custom profiles, A4, will use 16 and up).</summary>
public static class ProfileCode
{
    public const byte Unknown = 255;
    private static readonly string[] Names = ["EcAuto", "Quiet", "Balanced", "Performance", "Manual"];

    public static byte Of(string name)
    {
        int i = Array.IndexOf(Names, name);
        return i < 0 ? Unknown : (byte)i;
    }

    public static string? NameOf(byte code) => code < Names.Length ? Names[code] : null;
}
