using Excalibur.Core.Hardware;

namespace Excalibur.Core.Graphics;

/// <summary>The BIOS's graphics mode (WMI 0x0203; the stock Control Center: Hibrit / Harici / Dahili Mod).</summary>
public enum GraphicsMode
{
    /// <summary>Both GPUs, the NVIDIA one on demand (BIOS Primary Display "HG").</summary>
    Hybrid = 1,
    /// <summary>NVIDIA only (Primary Display "PEG"); takes effect after a restart.</summary>
    Discrete = 2,
    /// <summary>Intel only: the NVIDIA GPU switched off in Windows (the BIOS stays on HG, marked in CMOS).</summary>
    Integrated = 3,
}

/// <summary>What the BIOS reports: the mode, and whether a cable is in the HDMI port (wired to the NVIDIA GPU).</summary>
public sealed record GraphicsReading(GraphicsMode Mode, bool HdmiConnected);

/// <summary>
/// Reads the graphics mode with GET 0x0203 (DSDT GWMI.WSAA, the same on BIOS CQ141 and CQ212): A2 = the mode, from the
/// BIOS's Primary Display of this boot (4 = HG → 1, 1 = PEG → 2), or 3 when Integrated is marked in CMOS (D5 = 0x55);
/// A3 = the HDMI cable (GPIO). A Discrete choice shows only after the restart. With Integrated marked, the read also
/// sets the EC's GFXT to 0 (the EC stops reading the GPU temperature), as the Integrated SET does. Never read 0x0202
/// for anything: its read writes CMOS 0xB0 and the EC's DDSS.
/// </summary>
public sealed class GraphicsModeReader(ISmiChannel firmware)
{
    public const ushort Function = 0x0203;

    /// <summary>The reading, or null when this firmware does not answer it (a G770 echoes the request, A2 = 0).</summary>
    public GraphicsReading? Read() => Parse(firmware.Execute(SmiCommand.Get(Function)));

    public static GraphicsReading? Parse(SmiCommand reply) =>
        reply.A2 is >= 1 and <= 3 ? new GraphicsReading((GraphicsMode)reply.A2, reply.A3 == 1) : null;
}

/// <summary>
/// The last graphics mode reading; read at start, after a wake and after the firmware's HDMI event. Its HDMI flag is only
/// as fresh as the read: the BIOS raises the event when a cable goes in, not when it comes out (2026-11-02), so a
/// decision that needs the cable reads again first, as the stock Control Center does.
/// </summary>
public sealed class GraphicsModeMonitor(Func<GraphicsReading?> read, Action<string> log)
{
    private readonly object _gate = new();
    private bool _answered;

    /// <summary>The last reading; null before the first or when the firmware does not answer.</summary>
    public GraphicsReading? Current { get; private set; }

    /// <summary>Reads again; a change (or the first answer) goes to the log. A failed read keeps the last reading.</summary>
    public void Refresh(string why)
    {
        GraphicsReading? now;
        try { now = read(); }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            log($"graphics: mode not read ({why}): {ex.Message}");
            return;
        }
        lock (_gate)
        {
            bool same = _answered && now?.Mode == Current?.Mode;
            _answered = true;
            Current = now;
            if (same) return;
        }
        log(now is null
            ? $"graphics: this BIOS does not report the graphics mode ({why})"
            : $"graphics: {now.Mode} ({why})");
    }

    // WMI's failures (a stalled call is an InvalidOperationException); anything else is a bug and is not hidden.
    private static bool IsReadFailure(Exception ex) =>
#if !LINUX
        ex is System.Management.ManagementException ||
#endif
        ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException;
}
