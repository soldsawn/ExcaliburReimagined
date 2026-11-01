using Excalibur.Core.Fan;

namespace Excalibur.Linux;

/// <summary>
/// The fans on Linux (L1). From the EC (ec_sys): targets 0x2C/0x2D, the percent the EC drives each fan at 0x2E/0x2F, Fn+1
/// at 0xC3, the GPU temperature at 0xB7 (VGAT, as the BIOS's GET 0x0200 reads it), the firmware version at 0xFA.
/// The CPU temperature from the kernel's coretemp sensor (the CPU itself, like the MSR on Windows). The fan speeds from the
/// EC's memory window (0xFE400530, where the BIOS reads them; /dev/mem), else from the percent. Targets are read back after
/// writing: on Linux the kernel queues EC access, so this is safe here.
/// </summary>
internal sealed class LinuxFanHardware(LinuxEc ec, Action<string>? log = null) : IFanHardware
{
    private const byte GpuTemp = 0xB7, EcCpuTemp = 0x58;
    private const long FanSpeedAddress = 0xFE400530; // FS1H FS1L FS2H FS2L (high byte first)
    private const int MaxTempC = 110, MaxRpm = 8000;
    private readonly string? _coretemp = FindCoretemp();
    private bool _rpmFromMemory = true;

    // Windows uses SET 0x0205 through WMI where a BIOS supports it; Linux has no way to call it without a kernel module.
    public FanWritePath WritePath { get; set; }

    public FanSnapshot Read()
    {
        try
        {
            int cpuC = CoretempC() ?? ec.Read(EcCpuTemp);
            int gpuRaw = ec.Read(GpuTemp);
            // The EC reports 0 or 30 while the GPU sleeps or has no reading (Windows: ResolveGpuTemperature); the NVIDIA
            // driver is asked only then, and when it has none either the GPU fan follows the CPU.
            bool gpuPlaceholder = gpuRaw is 0 or 30;
            int? driverGpuC = gpuPlaceholder ? Excalibur.Core.Power.Nvml.TemperatureC() : null;
            bool gpuOff = gpuPlaceholder && driverGpuC is not (>= 1 and <= MaxTempC);
            int gpuC = gpuPlaceholder ? (gpuOff ? cpuC : driverGpuC!.Value) : gpuRaw;
            int cpuApplied = ec.Read(FanRegisters.CpuApplied), gpuApplied = ec.Read(FanRegisters.GpuApplied);
            var (cpuRpm, gpuRpm) = Rpm() ?? (cpuApplied * 54, gpuApplied * 52);
            var cpuTarget = FanTarget.FromRaw(ec.Read(FanRegisters.CpuTarget)) ?? FanTarget.Auto;
            var gpuTarget = FanTarget.FromRaw(ec.Read(FanRegisters.GpuTarget)) ?? FanTarget.Auto;
            bool boost = (ec.Read(FanRegisters.CoolingBoost) & 1) != 0;
            if (cpuC is < 1 or > MaxTempC || gpuC is < 1 or > MaxTempC)
                throw new FanReadException($"Implausible temperatures CPU={cpuC} GPU={gpuC}.");
            if (cpuApplied > 100 || gpuApplied > 100)
                throw new FanReadException($"Implausible percents CPU={cpuApplied} GPU={gpuApplied}.");
            return new FanSnapshot(cpuC, gpuC, cpuRpm, gpuRpm, cpuTarget, gpuTarget, cpuApplied, gpuApplied, boost, gpuOff);
        }
        catch (IOException ex) { throw new FanReadException("EC read failed.", ex); }
    }

    public void SetTarget(FanId fan, FanTarget target)
    {
        if (WritePath == FanWritePath.Wmi) throw new FanWriteException("The BIOS fan command (WMI 0x0205) is not available on Linux.");
        byte reg = FanRegisters.TargetOf(fan);
        if (!FanRegisters.IsWritable(reg, target.Value))
            throw new InvalidOperationException($"Refusing to write 0x{target.Value:X2} to 0x{reg:X2}.");
        try
        {
            ec.Write(reg, target.Value);
            if (ec.Read(reg) != target.Value) throw new FanWriteException($"{fan} fan target {target} did not read back from 0x{reg:X2}.");
        }
        catch (IOException ex) { throw new FanWriteException($"{fan} fan target {target} could not be written.", ex); }
    }

    public string? ReadEcVersion()
    {
        try
        {
            var chars = new char[FanRegisters.EcVersionLength];
            for (int i = 0; i < chars.Length; i++) chars[i] = (char)ec.Read((byte)(FanRegisters.EcVersionStart + i));
            string v = new(chars);
            return v.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z') ? v : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>The CPU package temperature from coretemp ("Package id 0"), or null.</summary>
    private int? CoretempC()
    {
        if (_coretemp is null) return null;
        try { return int.Parse(File.ReadAllText(_coretemp).Trim()) / 1000; }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException) { return null; }
    }

    private static string? FindCoretemp()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories("/sys/class/hwmon"))
            {
                if (File.ReadAllText(Path.Combine(dir, "name")).Trim() != "coretemp") continue;
                foreach (var label in Directory.GetFiles(dir, "temp*_label"))
                    if (File.ReadAllText(label).Trim() == "Package id 0") return label.Replace("_label", "_input");
                return Path.Combine(dir, "temp1_input");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Both fan speeds from the EC's memory window; null (and not tried again) when /dev/mem refuses.</summary>
    private (int Cpu, int Gpu)? Rpm()
    {
        if (!_rpmFromMemory) return null;
        try
        {
            using var mem = new FileStream("/dev/mem", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
            mem.Seek(FanSpeedAddress, SeekOrigin.Begin);
            var b = new byte[4];
            if (mem.Read(b, 0, 4) != 4) throw new IOException("short read");
            int cpu = b[0] << 8 | b[1], gpu = b[2] << 8 | b[3];
            if (cpu > MaxRpm || gpu > MaxRpm) throw new IOException($"implausible {cpu}/{gpu}");
            return (cpu, gpu);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _rpmFromMemory = false;
            log?.Invoke($"fan speeds: not readable from the EC memory ({ex.Message}); estimated from the percent");
            return null;
        }
    }
}
