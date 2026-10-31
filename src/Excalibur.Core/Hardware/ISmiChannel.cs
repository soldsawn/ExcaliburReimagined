namespace Excalibur.Core.Hardware;

/// <summary>Sends one firmware command and returns its response; <see cref="GmWmiGateway"/> on the machine, a fake in tests.</summary>
public interface ISmiChannel
{
    SmiCommand Execute(SmiCommand request);
}
