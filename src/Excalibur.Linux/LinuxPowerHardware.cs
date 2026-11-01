using System.Diagnostics;
using System.Globalization;
using Excalibur.Core.Power;

namespace Excalibur.Linux;

/// <summary>
/// Power on Linux (L2): the performance mode through EC register 0x95 (APMD: 0 = HighPerformance, 1 = Gaming, 2 = Office,
/// which sets the EC's internal mode, CPU PL1 and stock fan table) and, when present, /sys/firmware/acpi/platform_profile;
/// the power source from /sys/class/power_supply; the CPU's power and PL1/PL2 limits from the kernel's intel-rapl
/// powercap interface; the GPU's power and clock lock from NVML (libnvidia-ml.so.1).
/// Not done here: the BIOS's own mode call (DSDT WSAA 0xFB00/0x0300) also sets the NVIDIA Dynamic Boost values (NPCF
/// CTGP/DTGP/ATPP...) and the DPTF variables (ODV0/ODV1) and notifies NPCF and IETM; user space cannot run that ACPI
/// method, so on Linux the GPU's power ceiling does not follow the mode (planned for the L4 kernel module).
/// </summary>
internal sealed class LinuxPowerHardware(
    LinuxEc? ec,
    LinuxRapl? rapl = null,
    GpuLimiter? gpuLimiter = null,
    string powerSupplyRoot = "/sys/class/power_supply",
    string platformProfilePath = "/sys/firmware/acpi/platform_profile") : IPowerHardware
{
    public const byte EcPowerModeRegister = 0x95;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public void SetMode(PowerMode mode)
    {
        bool wroteProfile = TryWritePlatformProfile(mode);
        if (ec is not null)
        {
            try
            {
                ec.Write(EcPowerModeRegister, (byte)mode);
                return;
            }
            catch (IOException ex)
            {
                if (!wroteProfile) throw new PowerException($"EC 0x{EcPowerModeRegister:X2} could not be written ({ex.Message}).", ex);
            }
        }
        else if (!wroteProfile)
        {
            throw new PowerException("EC is not open and platform_profile is not available.");
        }
    }

    public PowerSource ReadSource()
    {
        try
        {
            if (!Directory.Exists(powerSupplyRoot)) return new PowerSource(false, null, false);
            bool? mainsOnline = null;
            bool? batDischarging = null;
            int? batPercent = null;

            foreach (var dir in Directory.GetDirectories(powerSupplyRoot))
            {
                string? type = ReadTrim(Path.Combine(dir, "type"));
                if (string.Equals(type, "Mains", StringComparison.OrdinalIgnoreCase))
                {
                    if (ReadTrim(Path.Combine(dir, "online")) is { } online)
                        mainsOnline = (mainsOnline ?? false) || online == "1";
                }
                else if (string.Equals(type, "Battery", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(ReadTrim(Path.Combine(dir, "capacity")), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cap) && cap is >= 0 and <= 100)
                        batPercent ??= cap;
                    if (ReadTrim(Path.Combine(dir, "status")) is { } status)
                        batDischarging ??= string.Equals(status, "Discharging", StringComparison.OrdinalIgnoreCase);
                }
            }

            bool onBattery = mainsOnline.HasValue ? !mainsOnline.Value : (batDischarging ?? false);
            return new PowerSource(onBattery, batPercent, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PowerException($"Could not read {powerSupplyRoot} ({ex.Message}).", ex);
        }
    }

    public int? ReadGpuWatts() => Nvml.EnforcedPowerLimitWatts();

    public int? ReadGpuPowerWatts() => Nvml.PowerUsageWatts();

    public CpuPower? ReadCpuPower()
    {
        if (rapl is null) return null;
        int? ecModePl1 = null;
        if (ec is not null)
        {
            try
            {
                ecModePl1 = ec.Read(EcPowerModeRegister) switch
                {
                    0 => 55,
                    1 => 45,
                    2 => 35,
                    _ => (int?)null,
                };
            }
            catch (IOException) { }
        }
        return rapl.Read(_clock.Elapsed, ecModePl1);
    }

    public void ApplyCpuLimit(CpuLimit? limit)
    {
        if (rapl is null)
        {
            if (limit is null) return;
            throw new PowerException("CPU limits are not available (intel-rapl not found).");
        }
        rapl.Apply(limit);
    }

    public int? ApplyGpuLimit(int? watts)
    {
        if (gpuLimiter is null) return null;
        gpuLimiter.Apply(watts);
        return gpuLimiter.LockMhz;
    }

    private bool TryWritePlatformProfile(PowerMode mode)
    {
        if (!File.Exists(platformProfilePath)) return false;
        string value = mode switch
        {
            PowerMode.HighPerformance => "performance",
            PowerMode.Gaming => "balanced",
            _ => "low-power",
        };
        try
        {
            File.WriteAllText(platformProfilePath, value);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? ReadTrim(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Reads the CPU package energy counter and reads/writes its PL1/PL2 power limits through the Linux kernel's intel-rapl
/// powercap sysfs (/sys/class/powercap/intel-rapl:0). Captures the original limits before the first write and puts them
/// back when the limit is cleared or the service stops.
/// </summary>
internal sealed class LinuxRapl(string powercapRoot = "/sys/class/powercap")
{
    private readonly string? _pkgDir = FindPackageDir(powercapRoot, mmio: false);
    private readonly string? _mmioPkgDir = FindPackageDir(powercapRoot, mmio: true);
    private (ulong EnergyUj, TimeSpan At)? _last;
    private ulong? _originalPl1Uw, _originalPl2Uw, _originalMmioPl1Uw;

    public CpuPower? Read(TimeSpan now, int? fallbackModePl1Watts = null)
    {
        if (_pkgDir is null) return null;
        try
        {
            int? watts = null;
            if (ReadUlong(Path.Combine(_pkgDir, "energy_uj")) is { } energy)
            {
                if (_last is { } last && now > last.At)
                {
                    ulong delta = energy >= last.EnergyUj
                        ? energy - last.EnergyUj
                        : (ReadUlong(Path.Combine(_pkgDir, "max_energy_range_uj")) is { } max && max > last.EnergyUj
                            ? (max - last.EnergyUj) + energy
                            : 0);
                    watts = (int)Math.Round(delta / 1_000_000.0 / (now - last.At).TotalSeconds);
                }
                _last = (energy, now);
            }

            int? msrPl1 = ReadUwAsWatts(Path.Combine(_pkgDir, "constraint_0_power_limit_uw"));
            int? msrPl2 = ReadUwAsWatts(Path.Combine(_pkgDir, "constraint_1_power_limit_uw"));
            int? mmioPl1 = (_mmioPkgDir is { } md ? ReadUwAsWatts(Path.Combine(md, "constraint_0_power_limit_uw")) : null)
                           ?? fallbackModePl1Watts;

            int? pl1 = Lower(msrPl1, mmioPl1);
            int? modePl1 = mmioPl1 ?? (_originalPl1Uw is { } orig ? (int)Math.Round(orig / 1_000_000.0) : msrPl1);
            return new CpuPower(watts, pl1, msrPl2, modePl1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Apply(CpuLimit? limit)
    {
        if (limit is null && _originalPl1Uw is null && _originalPl2Uw is null) return;
        if (_pkgDir is null)
        {
            if (limit is null) return;
            throw new PowerException("CPU power limits are not available (intel-rapl package domain not found).");
        }

        string pl1Path = Path.Combine(_pkgDir, "constraint_0_power_limit_uw");
        string pl2Path = Path.Combine(_pkgDir, "constraint_1_power_limit_uw");
        try
        {
            ulong curPl1 = ReadUlong(pl1Path) ?? throw new PowerException("Could not read constraint_0_power_limit_uw.");
            ulong curPl2 = ReadUlong(pl2Path) ?? throw new PowerException("Could not read constraint_1_power_limit_uw.");
            _originalPl1Uw ??= curPl1;
            _originalPl2Uw ??= curPl2;

            ulong wantedPl1 = limit is null ? _originalPl1Uw.Value : (ulong)limit.Pl1Watts * 1_000_000UL;
            ulong wantedPl2 = limit is null ? _originalPl2Uw.Value : (ulong)limit.Pl2Watts * 1_000_000UL;

            if (curPl1 != wantedPl1) File.WriteAllText(pl1Path, wantedPl1.ToString(CultureInfo.InvariantCulture));
            if (curPl2 != wantedPl2) File.WriteAllText(pl2Path, wantedPl2.ToString(CultureInfo.InvariantCulture));

            if (_mmioPkgDir is { } md)
            {
                string mmioPl1Path = Path.Combine(md, "constraint_0_power_limit_uw");
                if (ReadUlong(mmioPl1Path) is { } curMmioPl1)
                {
                    _originalMmioPl1Uw ??= curMmioPl1;
                    ulong wantedMmioPl1 = limit is null ? _originalMmioPl1Uw.Value : wantedPl1;
                    if (curMmioPl1 != wantedMmioPl1) File.WriteAllText(mmioPl1Path, wantedMmioPl1.ToString(CultureInfo.InvariantCulture));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PowerException($"Could not write CPU power limits ({ex.Message}).", ex);
        }
    }

    private static int? Lower(int? a, int? b) => a is { } x && b is { } y ? Math.Min(x, y) : a ?? b;

    private static int? ReadUwAsWatts(string path) =>
        ReadUlong(path) is { } uw and > 0 ? (int)Math.Round(uw / 1_000_000.0) : null;

    private static ulong? ReadUlong(string path)
    {
        try
        {
            return File.Exists(path) && ulong.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong v)
                ? v
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? FindPackageDir(string root, bool mmio)
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            foreach (var dir in Directory.GetDirectories(root, "intel-rapl*"))
            {
                bool isMmio = Path.GetFileName(dir).StartsWith("intel-rapl-mmio", StringComparison.OrdinalIgnoreCase);
                if (isMmio != mmio) continue;
                string namePath = Path.Combine(dir, "name");
                if (File.Exists(namePath) && File.ReadAllText(namePath).Trim().StartsWith("package", StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
