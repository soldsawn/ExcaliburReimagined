namespace Excalibur.Core.Hardware;

/// <summary>
/// The 32-byte command/response block exchanged with the firmware through
/// <c>root\wmi:RW_GMWMI.BufferBytes</c>. Layout is little-endian (see docs/02):
/// <code>
///   u16 A0  (0xFA00 = GET, 0xFB00 = SET)
///   u16 A1  (function code, e.g. 0x0200 sensors, 0x0100 LED, 0x0300 perf mode)
///   u32 A2..A6  (arguments / return values)
///   u32 Rev0, Rev1 (unused)
/// </code>
/// </summary>
public struct SmiCommand
{
    public const int SizeBytes = 32;

    public const ushort OpGet = 0xFA00;
    public const ushort OpSet = 0xFB00;

    public ushort A0;
    public ushort A1;
    public uint A2;
    public uint A3;
    public uint A4;
    public uint A5;
    public uint A6;

    public static SmiCommand Get(ushort function) => new() { A0 = OpGet, A1 = function };
    public static SmiCommand Set(ushort function) => new() { A0 = OpSet, A1 = function };

    public readonly byte[] ToBytes()
    {
        var b = new byte[SizeBytes];
        BitConverter.GetBytes(A0).CopyTo(b, 0);
        BitConverter.GetBytes(A1).CopyTo(b, 2);
        BitConverter.GetBytes(A2).CopyTo(b, 4);
        BitConverter.GetBytes(A3).CopyTo(b, 8);
        BitConverter.GetBytes(A4).CopyTo(b, 12);
        BitConverter.GetBytes(A5).CopyTo(b, 16);
        BitConverter.GetBytes(A6).CopyTo(b, 20);
        return b;
    }

    public static SmiCommand FromBytes(byte[] b) => new()
    {
        A0 = BitConverter.ToUInt16(b, 0),
        A1 = BitConverter.ToUInt16(b, 2),
        A2 = BitConverter.ToUInt32(b, 4),
        A3 = BitConverter.ToUInt32(b, 8),
        A4 = BitConverter.ToUInt32(b, 12),
        A5 = BitConverter.ToUInt32(b, 16),
        A6 = BitConverter.ToUInt32(b, 20),
    };
}
