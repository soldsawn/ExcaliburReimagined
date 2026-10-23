namespace Excalibur.Core.Hardware;

/// <summary>The --simulate stand-in for the firmware: takes every command and answers zeros.</summary>
public sealed class SimulatedSmiChannel : ISmiChannel
{
    public SmiCommand Execute(SmiCommand request) => new() { A0 = request.A0, A1 = request.A1 };
}
