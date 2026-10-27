namespace Excalibur.Core.Fan;

/// <summary>
/// In-memory stand-in for <see cref="EcFanHardware"/> with a crude thermal model, for
/// tests and for running the service without touching the EC. Call
/// <see cref="Advance"/> to move simulated time. With a target of Auto it follows the
/// measured stock EC curve (docs/07 A.2).
/// </summary>
public sealed class FakeFanHardware : IFanHardware
{
    public double CpuTempC { get; set; } = 48;
    public double GpuTempC { get; set; } = 45;
    /// <summary>Heat input 0-1 per chip (0 = idle).</summary>
    public double CpuLoad { get; set; }
    public double GpuLoad { get; set; }
    public bool CoolingBoost { get; set; }
    public string? EcVersion { get; set; } = "P008B0";

    public FanTarget CpuTarget { get; private set; } = FanTarget.Auto;
    public FanTarget GpuTarget { get; private set; } = FanTarget.Auto;

    /// <summary>Fail the next N reads / writes.</summary>
    public int FailNextReads { get; set; }
    public int FailNextWrites { get; set; }
    /// <summary>Report the EC busy (nothing written) on the next N writes.</summary>
    public int BusyNextWrites { get; set; }

    public List<(FanId Fan, FanTarget Target)> Writes { get; } = [];

    public FanWritePath WritePath { get; set; }

    /// <summary>Whether the BIOS acts on WMI SET 0x0205; when not (the G870's BIOS), a WMI write changes nothing.</summary>
    public bool WmiWorks { get; set; }

    /// <summary>The writes sent through WMI (<see cref="Writes"/> has the EC's).</summary>
    public List<(FanId Fan, FanTarget Target)> WmiWrites { get; } = [];

    /// <summary>A fan that ignores its target register (an EC firmware with another layout): it stays on the stock curve.</summary>
    public FanId? DeafFan { get; set; }

    public int AppliedPercent(FanId fan)
    {
        var target = fan == FanId.Cpu ? CpuTarget : GpuTarget;
        if (!target.IsAuto && fan != DeafFan) return target.Percent;
        double t = fan == FanId.Cpu ? CpuTempC : GpuTempC;
        int max = fan == FanId.Cpu ? 94 : 84;
        return t < 58 ? 66 : t < 65 ? 86 : max;
    }

    public FanSnapshot Read()
    {
        if (FailNextReads > 0)
        {
            FailNextReads--;
            throw new FanReadException("simulated read failure");
        }
        int cpu = AppliedPercent(FanId.Cpu), gpu = AppliedPercent(FanId.Gpu);
        return new FanSnapshot((int)Math.Round(CpuTempC), (int)Math.Round(GpuTempC), cpu * 54, gpu * 54,
            CpuTarget, GpuTarget, cpu, gpu, CoolingBoost);
    }

    public void SetTarget(FanId fan, FanTarget target)
    {
        if (!FanRegisters.IsWritable(FanRegisters.TargetOf(fan), target.Value))
            throw new InvalidOperationException($"Refusing {target} for {fan}.");
        if (FailNextWrites > 0)
        {
            FailNextWrites--;
            throw new FanWriteException("simulated write failure");
        }
        if (BusyNextWrites > 0)
        {
            BusyNextWrites--;
            throw new FanBusyException("simulated busy EC");
        }
        if (WritePath == FanWritePath.Wmi)
        {
            WmiWrites.Add((fan, target));
            if (!WmiWorks) return;
        }
        else Writes.Add((fan, target));
        if (fan == FanId.Cpu) CpuTarget = target; else GpuTarget = target;
    }

    public string? ReadEcVersion() => EcVersion;

    /// <summary>How often the loop took the no-read hand-back (start or stop on a verified EC).</summary>
    public int UnreadHandBacks { get; private set; }

    public void ResetToAutoUnread()
    {
        UnreadHandBacks++;
        this.ResetToAuto();
    }

    /// <summary>
    /// Moves each chip toward its equilibrium with a 20 s time constant. Calibrated to the
    /// measurements (docs/06-07): CPU idles at ~50 C on the stock 66% and ~55 C at 45%
    /// (66 - 0.24 * fan), and reaches ~97 C under full load at the stock 94%; the GPU idles
    /// at ~45 C on 63% and reaches ~85 C under load at 84%.
    /// </summary>
    public void Advance(TimeSpan dt)
    {
        double k = 1 - Math.Exp(-dt.TotalSeconds / 20);
        CpuTempC += (66 + 54 * CpuLoad - 0.24 * AppliedPercent(FanId.Cpu) - CpuTempC) * k;
        GpuTempC += (60 + 45 * GpuLoad - 0.24 * AppliedPercent(FanId.Gpu) - GpuTempC) * k;
    }
}
