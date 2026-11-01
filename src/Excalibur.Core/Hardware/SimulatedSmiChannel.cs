namespace Excalibur.Core.Hardware;

/// <summary>A firmware that echoes each request; the graphics mode reads as Hybrid without an HDMI cable.</summary>
public sealed class SimulatedSmiChannel : ISmiChannel
{
    public SmiCommand Execute(SmiCommand request) =>
        request is { A0: SmiCommand.OpGet, A1: Graphics.GraphicsModeReader.Function }
            ? new() { A0 = request.A0, A1 = request.A1, A2 = 1, A4 = 1 }
            : new() { A0 = request.A0, A1 = request.A1 };
}
