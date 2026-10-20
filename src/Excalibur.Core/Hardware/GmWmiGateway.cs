using System.Management;

namespace Excalibur.Core.Hardware;

/// <summary>
/// Talks to the firmware's ACPI WMI method <c>\_SB.GWMI</c>, exposed to Windows as
/// <c>root\wmi:RW_GMWMI</c>. Writing <c>BufferBytes</c> runs the BIOS handler (WSAA);
/// reading it back returns the response (WQAA). Works without administrator rights.
/// </summary>
public sealed class GmWmiGateway
{
    private const string Scope = @"root\wmi";
    private const string ClassName = "RW_GMWMI";
    private readonly object _gate = new();

    /// <summary>True when the WMI class exists on this machine.</summary>
    public bool IsAvailable()
    {
        try { return GetInstance() is not null; }
        catch (ManagementException) { return false; }
    }

    /// <summary>Sends one command and returns the firmware's response block.</summary>
    public SmiCommand Execute(SmiCommand request)
    {
        // The firmware keeps a single shared response buffer, so requests must not interleave.
        lock (_gate)
        {
            using var instance = GetInstance()
                ?? throw new InvalidOperationException($"{ClassName} is not present on this system.");
            instance["BufferBytes"] = request.ToBytes();
            instance.Put();

            using var reply = GetInstance()
                ?? throw new InvalidOperationException($"{ClassName} disappeared after write.");
            return SmiCommand.FromBytes((byte[])reply["BufferBytes"]);
        }
    }

    private static ManagementObject? GetInstance()
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(Scope), new SelectQuery(ClassName));
        foreach (ManagementObject obj in searcher.Get())
            return obj;
        return null;
    }
}
