using Excalibur.Core.Hardware;

namespace Excalibur.Linux;

/// <summary>
/// Implements the BIOS's WSAA 0x0100 (keyboard lighting) and GET 0x0200 A6 (light key brightness) directly over the EC's
/// ACPI registers (DSDT Method WSAA, lines 104846-105000), so <see cref="Excalibur.Core.Lighting.KeyboardLight"/> and
/// <see cref="Excalibur.Core.Lighting.LightingManager"/> work identically on Linux without needing a WMI kernel driver.
/// </summary>
internal sealed class LinuxEcSmiChannel(Func<byte, byte> readEc, Action<byte, byte> writeEc, Action<string>? log = null) : ISmiChannel
{
    public const byte RegKeyLevel = 0x48;   // LKCL (0 -> level 1, 1 -> level 2, 2 -> level 0)
    public const byte RegRed = 0x89;        // LKBR
    public const byte RegGreen = 0x8A;      // LKBG
    public const byte RegBlue = 0x8B;       // LKBB
    public const byte RegControl = 0xAC;    // [LKBA:4 (bits 4..7)][LKBS:1 (bit 3)][LKBC:1 (bit 2)][LKBE:1 (bit 1)][LKCC:1 (bit 0)]
    public const byte RegModeLevel = 0xC1;  // [LKBM:4 (bits 4..7)][LKBL:4 (bits 0..3)]

    private const byte BitLkcc = 0x01, BitLkbe = 0x02, BitLkbc = 0x04, BitLkbs = 0x08;
    private const int HandshakePolls = 100;
    private readonly object _gate = new();

    public LinuxEcSmiChannel(LinuxEc ec, Action<string>? log = null) : this(ec.Read, ec.Write, log) { }

    public SmiCommand Execute(SmiCommand request)
    {
        lock (_gate)
        {
            if (request.A0 == SmiCommand.OpSet && request.A1 == 0x0100)
            {
                ApplyLed(request.A2, request.A3);
                return new SmiCommand();
            }
            if (request.A0 == SmiCommand.OpGet && request.A1 == 0x0200)
            {
                var reply = request;
                reply.A6 = (uint)ReadLightKeyBrightness();
                return reply;
            }
            return request;
        }
    }

    /// <summary>
    /// Reads the keyboard light key's brightness level from EC 0x48 (LKCL), mapped the same way DSDT GET 0x0200 maps it:
    /// 0 -> 1, 1 -> 2, 2 -> 0.
    /// </summary>
    public int ReadLightKeyBrightness() => readEc(RegKeyLevel) switch
    {
        0 => 1,
        1 => 2,
        2 => 0,
        var v => Math.Clamp((int)v, 0, 2),
    };

    private void ApplyLed(uint zoneRaw, uint data)
    {
        byte zone = (byte)(zoneRaw & 0x0F);
        byte blue = (byte)(data & 0xFF);
        byte green = (byte)((data >> 8) & 0xFF);
        byte red = (byte)((data >> 16) & 0xFF);
        byte brightness = (byte)((data >> 24) & 0x0F);
        byte effect = (byte)((data >> 28) & 0x0F);

        // DSDT WSAA 0x0100: Case(0)->LKBL=2, Case(1)->LKBL=0, Case(2)->LKBL=1.
        byte lkbl = brightness switch
        {
            0 => 2,
            1 => 0,
            _ => 1,
        };

        // The BIOS writes each 0xAC bit field on its own (read-modify-write), so the other bits keep their value;
        // do the same, in the same order. LKBC = 0, then the zone (LKBA), then RGB and mode/level.
        UpdateControl(c => (byte)(c & ~BitLkbc));
        UpdateControl(c => (byte)((c & 0x0F) | (zone << 4)));
        writeEc(RegBlue, blue);
        writeEc(RegGreen, green);
        writeEc(RegRed, red);
        writeEc(RegModeLevel, (byte)((effect << 4) | (lkbl & 0x0F)));

        // Handshake 1: set LKCC = 1, wait for EC to set LKBE = 1.
        UpdateControl(c => (byte)(c | BitLkcc));
        WaitForAck("colour");

        // Handshake 2: clear LKBE, then set LKBS (LKCC stays as it is), wait for EC to set LKBE = 1.
        UpdateControl(c => (byte)(c & ~BitLkbe));
        UpdateControl(c => (byte)(c | BitLkbs));
        WaitForAck("apply");
    }

    private void UpdateControl(Func<byte, byte> change) => writeEc(RegControl, change(readEc(RegControl)));

    private void WaitForAck(string step)
    {
        for (int i = 0; i < HandshakePolls; i++)
        {
            if ((readEc(RegControl) & BitLkbe) != 0) return;
            Thread.Sleep(1);
        }
        // The BIOS waits forever here; we give up so the service never hangs, and say so.
        log?.Invoke($"lighting: EC did not acknowledge the {step} step (0x{RegControl:X2} bit 1) within {HandshakePolls} polls");
    }
}
