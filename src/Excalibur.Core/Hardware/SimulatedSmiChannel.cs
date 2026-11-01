using Excalibur.Core.Graphics;

namespace Excalibur.Core.Hardware;

/// <summary>
/// A firmware that echoes each request, except the graphics mode (0x0203): it starts in Hybrid without an HDMI cable,
/// takes Hybrid and Integrated at once and Discrete only "after a restart" (never, here), as the BIOS does; started in
/// Discrete, every change waits for the restart (the boot mode comes from the BIOS's Primary Display).
/// </summary>
public sealed class SimulatedSmiChannel : ISmiChannel
{
    public GraphicsMode Mode { get; set; } = GraphicsMode.Hybrid;
    public bool HdmiConnected { get; set; }
    /// <summary>The last mode written (SET), whether or not it shows before a restart.</summary>
    public GraphicsMode? LastSet { get; private set; }

    public SmiCommand Execute(SmiCommand request)
    {
        if (request.A1 != GraphicsModeReader.Function) return new() { A0 = request.A0, A1 = request.A1 };
        if (request.A0 == SmiCommand.OpSet)
        {
            LastSet = (GraphicsMode)request.A2;
            if (Mode != GraphicsMode.Discrete)
                // Booted Hybrid: Integrated shows at once (its CMOS mark); Hybrid and Discrete clear the mark, so it reads
                // Hybrid until the restart (DSDT: SAPD 4 → 1 unless CMOS D5 = 0x55).
                Mode = LastSet == GraphicsMode.Integrated ? GraphicsMode.Integrated : GraphicsMode.Hybrid;
        }
        return new() { A0 = request.A0, A1 = request.A1, A2 = (uint)Mode, A3 = HdmiConnected ? 1u : 0u, A4 = (uint)Mode };
    }
}

/// <summary>An NVIDIA GPU that switches off and on when asked (simulation and tests).</summary>
public sealed class SimulatedGpuDevices : IGpuDevices
{
    public bool? Enabled { get; private set; } = true;
    public string? FailWith { get; set; }
    public int Disables, Enables;

    public void Disable()
    {
        Disables++;
        if (FailWith is { } why) throw new GraphicsException("graphics_devices", why);
        Enabled = false;
    }

    public void Enable()
    {
        Enables++;
        Enabled = true;
    }
}
