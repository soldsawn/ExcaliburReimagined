namespace Excalibur.Core.Fan;

/// <summary>What the fan loop needs from the machine. Real: <see cref="EcFanHardware"/>.</summary>
public interface IFanHardware
{
    /// <summary>Reads and validates one snapshot; throws <see cref="FanReadException"/> when it cannot be trusted.</summary>
    FanSnapshot Read();

    /// <summary>Writes one fan target (the real hardware checks it by the RPM afterwards); throws <see cref="FanWriteException"/>.</summary>
    void SetTarget(FanId fan, FanTarget target);

    /// <summary>The EC firmware version string (e.g. "P008B0"), or null when unreadable.</summary>
    string? ReadEcVersion();
}

public static class FanHardwareExtensions
{
    /// <summary>Hands both fans back to the EC. Tries both even if the first fails.</summary>
    public static void ResetToAuto(this IFanHardware hw)
    {
        Exception? first = null;
        foreach (var fan in new[] { FanId.Cpu, FanId.Gpu })
        {
            try { hw.SetTarget(fan, FanTarget.Auto); }
            catch (Exception ex) { first ??= ex; }
        }
        if (first is not null) throw new FanWriteException("Could not hand the fans back to the EC.", first);
    }
}
