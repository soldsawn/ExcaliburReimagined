using Excalibur.Core.Power;

namespace Excalibur.Core.Hardware;

/// <summary>
/// <see cref="ICpuPowerRegisters"/> through PawnIO's official IntelMSR module (read only: this class
/// never calls its write) and IntelMCHBAR module (which has no write at all).
/// </summary>
public sealed class PawnIoCpuPowerRegisters : ICpuPowerRegisters, IDisposable
{
    private const ulong MchbarPackageLimit = 0x59A0;
    private readonly PawnIoSession _msr;
    private readonly PawnIoSession? _mchbar;

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

    public ulong ReadMsr(uint index) => _msr.Execute("ioctl_read_msr", [index], 1)[0];

    public ulong? ReadMchbarLimit()
    {
        if (_mchbar is null) return null;
        try { return _mchbar.Execute("ioctl_read_qword", [MchbarPackageLimit], 1)[0]; }
        catch (InvalidOperationException) { return null; }
    }

    public void Dispose()
    {
        _msr.Dispose();
        _mchbar?.Dispose();
    }
}
