using System.Runtime.InteropServices;
using Excalibur.Core.Hardware;

namespace Excalibur.Core.Power;

/// <summary>
/// The real power hardware: the performance mode through the firmware's WMI method (SET 0x0300,
/// a2 = mode; works without administrator rights, docs/03), the power source from Windows plus the
/// USB-C adapter flag (GET 0x0206, a4 = 1), the GPU limit and draw from the NVIDIA driver (NVML) and, when given a
/// <see cref="CpuPowerMeter"/> and <see cref="CpuLimiter"/>, the CPU's power and limits.
/// Shares the <see cref="GmWmiGateway"/> with the fan hardware: the firmware has one response buffer.
/// </summary>
public sealed class EcPowerHardware(GmWmiGateway wmi, CpuPowerMeter? cpu = null, CpuLimiter? cpuLimiter = null,
    GpuLimiter? gpuLimiter = null) : IPowerHardware
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public CpuPower? ReadCpuPower() => cpu?.Read(_clock.Elapsed);

    public int? ReadGpuPowerWatts() => Nvml.PowerUsageWatts();

    public void ApplyCpuLimit(CpuLimit? limit)
    {
        if (cpuLimiter is null)
        {
            if (limit is null) return;
            throw new PowerException("CPU limits are not available (PawnIO IntelMSR module missing).");
        }
        cpuLimiter.Apply(limit);
    }

    public int? ApplyGpuLimit(int? watts)
    {
        if (gpuLimiter is null) return null;
        gpuLimiter.Apply(watts);
        return gpuLimiter.LockMhz;
    }

    private const ushort PerformanceModeFunction = 0x0300, AdapterFunction = 0x0206;
    // One more look this long after plugging in, in case the flag settled late; after that the adapter cannot change
    // without a spell on battery. Rechecking every 30 s cost 360 firmware calls an hour on the wall (2026-10-26).
    private static readonly TimeSpan AdapterRecheck = TimeSpan.FromSeconds(30);

    private bool _lastOnBattery = true;
    private bool _usbC;
    private DateTime _adapterReadAt = DateTime.MinValue;
    private bool _adapterRechecked;

    public void SetMode(PowerMode mode)
    {
        try
        {
            var command = SmiCommand.Set(PerformanceModeFunction);
            command.A2 = (uint)mode;
            wmi.Execute(command);
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or InvalidOperationException or COMException)
        {
            throw new PowerException(ex.Message, ex);
        }
    }

    public PowerSource ReadSource()
    {
        if (!GetSystemPowerStatus(out var status)) throw new PowerException("GetSystemPowerStatus failed.");
        // 0 = on battery, 1 = on the wall, 255 = unknown (treated as the wall: a desktop-like machine).
        bool onBattery = status.ACLineStatus == 0;
        int? percent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null;

        // The USB-C flag is a WMI call (slower): read when the charger was just plugged in, and once more a little later.
        if (onBattery)
        {
            _usbC = false;
            _adapterReadAt = DateTime.MinValue;
        }
        else if (_lastOnBattery || !_adapterRechecked && DateTime.UtcNow - _adapterReadAt > AdapterRecheck)
        {
            _adapterRechecked = !_lastOnBattery;
            _usbC = ReadUsbCAdapter();
            _adapterReadAt = DateTime.UtcNow;
        }
        _lastOnBattery = onBattery;
        return new PowerSource(onBattery, percent, _usbC);
    }

    public int? ReadGpuWatts() => Nvml.EnforcedPowerLimitWatts();

    /// <summary>The stock software waits 100 ms and asks up to 5 times; the flag settles a moment after plugging in.</summary>
    private bool ReadUsbCAdapter()
    {
        try
        {
            for (int i = 0; i < 3; i++)
            {
                Thread.Sleep(100);
                if (wmi.Execute(SmiCommand.Get(AdapterFunction)).A4 == 1) return true;
            }
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or InvalidOperationException or COMException) { }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
