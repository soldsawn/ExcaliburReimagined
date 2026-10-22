using Excalibur.Core.Power;

namespace Excalibur.Core.Hardware;

/// <summary>
/// <see cref="ICpuPowerRegisters"/> through PawnIO's official IntelMSR module (written only by
/// <see cref="CpuLimiter"/>, for the package power limit) and IntelMCHBAR module (which has no write at all).
/// The power worker and the fan loop (CPU temperature) call it from different threads, so each call is serialized.
/// </summary>
public sealed class PawnIoCpuPowerRegisters : ICpuPowerRegisters, IDisposable
{
    private const ulong MchbarPackageLimit = 0x59A0;
    private readonly PawnIoSession _msr;
    private readonly PawnIoSession? _mchbar;
    private readonly object _gate = new();

    private PawnIoCpuPowerRegisters(PawnIoSession msr, PawnIoSession? mchbar)
    {
        _msr = msr;
        _mchbar = mchbar;
    }

    /// <summary>Loads both modules from <paramref name="moduleDirectory"/>; null when IntelMSR cannot be loaded.</summary>
    public static PawnIoCpuPowerRegisters? TryOpen(string moduleDirectory)
    {
        PawnIoSession? Load(string name)
        {
            try { return PawnIoSession.Load(File.ReadAllBytes(Path.Combine(moduleDirectory, name))); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or DllNotFoundException) { return null; }
        }
        return Load("IntelMSR.bin") is { } msr ? new PawnIoCpuPowerRegisters(msr, Load("IntelMCHBAR.bin")) : null;
    }

    public ulong ReadMsr(uint index)
    {
        lock (_gate) return _msr.Execute("ioctl_read_msr", [index], 1)[0];
    }

    public void WriteMsr(uint index, ulong value)
    {
        lock (_gate) _msr.Execute("ioctl_write_msr", [index, value], 0);
    }

    public ulong? ReadMchbarLimit()
    {
        if (_mchbar is null) return null;
        try { lock (_gate) return _mchbar.Execute("ioctl_read_qword", [MchbarPackageLimit], 1)[0]; }
        catch (InvalidOperationException) { return null; }
    }

    public void Dispose()
    {
        _msr.Dispose();
        _mchbar?.Dispose();
    }
}
