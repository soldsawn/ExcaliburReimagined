namespace Excalibur.Core.Fan;

/// <summary>How fan targets reach the EC: written to its registers, or handed to the BIOS (WMI SET 0x0205).</summary>
public enum FanWritePath { Ec, Wmi }

/// <summary>What the fan loop needs from the machine. Real: <see cref="EcFanHardware"/>.</summary>
public interface IFanHardware
{
    /// <summary>Reads and validates one snapshot; throws <see cref="FanReadException"/> when it cannot be trusted.</summary>
    FanSnapshot Read();

    /// <summary>Writes one fan target (the real hardware checks it by the RPM afterwards); throws <see cref="FanWriteException"/>.</summary>
    void SetTarget(FanId fan, FanTarget target);

    /// <summary>
    /// Where <see cref="SetTarget"/> writes. Wmi only where a trial saw the fans follow it (a BIOS whose 0x0205 handler is
    /// filled in; the G870's CQ141/CQ171/CQ212 leave it empty).
    /// </summary>
    FanWritePath WritePath { get; set; }

    /// <summary>The EC firmware version string (e.g. "P008B0"), or null when unreadable.</summary>
    string? ReadEcVersion();

    /// <summary>
    /// Hands both fans to the EC on a verified EC, at start (whose first tick writes the profile's targets right after)
    /// and at stop; the real hardware then reads nothing back (<see cref="EcFanHardware.ResetToAutoUnread"/>).
    /// </summary>
    void ResetToAutoUnread() => this.ResetToAuto();
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
