namespace Excalibur.Core.Fan;

/// <summary>One validated reading of everything the fan loop needs.</summary>
public sealed record FanSnapshot(
    int CpuTempC,
    int GpuTempC,
    int CpuRpm,
    int GpuRpm,
    FanTarget CpuTarget,
    FanTarget GpuTarget,
    int CpuAppliedPercent,
    int GpuAppliedPercent,
    bool CoolingBoost)
{
    public int TempOf(FanId fan) => fan == FanId.Cpu ? CpuTempC : GpuTempC;

    public FanTarget TargetOf(FanId fan) => fan == FanId.Cpu ? CpuTarget : GpuTarget;
}

/// <summary>The firmware's sensor block alone (temperatures and the percent worked out from RPM), no EC read.</summary>
public sealed record FanSensors(int CpuTempC, int GpuTempC, int CpuAppliedPercent, int GpuAppliedPercent);

/// <summary>A read that failed or did not pass the sanity checks; the loop counts these.</summary>
public sealed class FanReadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The EC was busy with another driver, so nothing was written; the loop tries again on a later tick.</summary>
public sealed class FanBusyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A write whose read-back did not match.</summary>
public sealed class FanWriteException(string message, Exception? inner = null) : Exception(message, inner);
