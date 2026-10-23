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
public sealed class EcPowerHardware(GmWmiGateway wmi, CpuPowerMeter? cpu = null, CpuLimiter? cpuLimiter = null) : IPowerHardware
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

    private const ushort PerformanceModeFunction = 0x0300, AdapterFunction = 0x0206;
    private static readonly TimeSpan AdapterRecheck = TimeSpan.FromSeconds(30);

    private bool _lastOnBattery = true;
    private bool _usbC;
    private DateTime _adapterReadAt = DateTime.MinValue;

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

        // The USB-C flag is a WMI call (slower): read when the charger was just plugged in, then now and then.
        if (onBattery)
        {
            _usbC = false;
            _adapterReadAt = DateTime.MinValue;
        }
        else if (_lastOnBattery || DateTime.UtcNow - _adapterReadAt > AdapterRecheck)
        {
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

/// <summary>Reads the GPU power limit the driver enforces and the power drawn now; every failure is "unknown" (null).</summary>
internal static class Nvml
{
    private static readonly object Gate = new();
    private static bool _tried, _ready;
    private static IntPtr _device;

    public static int? EnforcedPowerLimitWatts() => Milliwatts(nvmlDeviceGetEnforcedPowerLimit);

    public static int? PowerUsageWatts() => Milliwatts(nvmlDeviceGetPowerUsage);

    private delegate int MilliwattQuery(IntPtr device, out uint milliwatts);

    private static int? Milliwatts(MilliwattQuery query)
    {
        lock (Gate)
        {
            if (!_tried)
            {
                _tried = true;
                try
                {
                    _ready = nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out _device) == 0;
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
            }
            if (!_ready) return null;
            try
            {
                return query(_device, out uint milliwatts) == 0 ? (int)Math.Round(milliwatts / 1000.0) : null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
        }
    }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
}
