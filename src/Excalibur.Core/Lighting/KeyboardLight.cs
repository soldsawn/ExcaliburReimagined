using Excalibur.Core.Hardware;

namespace Excalibur.Core.Lighting;

/// <summary>The light effect, as the firmware takes it in the top 4 bits of the LED data (docs/02 §3).</summary>
public enum LightEffect
{
    Off = 0,
    Static = 1,
    Breathing = 3,
    ColorCycle = 6,
}

/// <summary>
/// Where a light command goes (a2 of SET 0x0100). The G870 (SKU 49) has only the keyboard's three zones;
/// the logo and the rear strip exist on other models.
/// </summary>
public enum LightZone
{
    All = 0,
    KeyboardRight = 3,
    KeyboardMiddle = 4,
    KeyboardLeft = 5,
    Keyboard = 6,
    RearStrip = 7,
    Logo = 8,
}

/// <summary>
/// One zone's light: <c>[effect:4][brightness:4][R:8][G:8][B:8]</c>. Brightness is 0-2, the levels of the
/// keyboard light key.
/// </summary>
public readonly record struct LightSetting(LightEffect Effect, int Brightness, int Red, int Green, int Blue)
{
    public const int MaxBrightness = 2;

    public static LightSetting Off => new(LightEffect.Off, 0, 0, 0, 0);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(Effect)) errors.Add($"unknown light effect {(int)Effect}");
        if (Brightness is < 0 or > MaxBrightness) errors.Add($"brightness must be 0-{MaxBrightness}");
        if (new[] { Red, Green, Blue }.Any(c => c is < 0 or > 255)) errors.Add("colour channels must be 0-255");
        return errors;
    }

    /// <summary>
    /// The 32-bit LED data. At brightness 0 the stock software sends grey 0x101010 in place of the colour,
    /// and so do we: the firmware's behaviour with a colour at level 0 is unknown.
    /// </summary>
    public uint Encode()
    {
        uint rgb = Brightness == 0 ? 0x101010u : (uint)(Red << 16 | Green << 8 | Blue);
        return (uint)Effect << 28 | (uint)Brightness << 24 | rgb;
    }

    public static LightSetting Decode(uint data) => new(
        (LightEffect)(data >> 28), (int)(data >> 24 & 0xF), (int)(data >> 16 & 0xFF), (int)(data >> 8 & 0xFF), (int)(data & 0xFF));
}

/// <summary>
/// The keyboard light through the firmware's WMI method (SET 0x0100, a2 = zone, a3 = LED data). Works without
/// administrator rights. A command equal to the last one sent to that zone is skipped, as the stock software does.
/// </summary>
public sealed class KeyboardLight(ISmiChannel wmi)
{
    private const ushort LedFunction = 0x0100, SensorFunction = 0x0200;
    private readonly Dictionary<LightZone, uint> _sent = [];

    public void Set(LightZone zone, LightSetting setting)
    {
        if (setting.Validate() is [var error, ..]) throw new ArgumentException(error, nameof(setting));
        uint data = setting.Encode();
        lock (_sent)
        {
            if (_sent.TryGetValue(zone, out uint last) && last == data) return;
            var command = SmiCommand.Set(LedFunction);
            command.A2 = (uint)zone;
            command.A3 = data;
            wmi.Execute(command);
            // "All" and "Keyboard" overwrite the zones they cover, so their cached values no longer hold.
            if (zone is LightZone.All or LightZone.Keyboard) _sent.Clear();
            _sent[zone] = data;
        }
    }

    /// <summary>Forgets what was sent, so the next command goes out even when equal (after sleep the firmware may have reset the light).</summary>
    public void Forget()
    {
        lock (_sent) _sent.Clear();
    }

    /// <summary>The level the keyboard light key last set (GET 0x0200 a6; the firmware reports 3 for 2 on some models).</summary>
    public int ReadKeyBrightness() => Math.Min((int)wmi.Execute(SmiCommand.Get(SensorFunction)).A6, LightSetting.MaxBrightness);

    /// <summary>GET 0x0100: the firmware's answer, raw (its meaning is not verified on this machine).</summary>
    public uint ReadRaw() => wmi.Execute(SmiCommand.Get(LedFunction)).A2;
}
