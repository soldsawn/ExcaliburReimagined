using System.Runtime.InteropServices;

namespace Excalibur.Core.Power;

/// <summary>The GPU's clocks through NVML; a refused lock is a <see cref="PowerException"/>.</summary>
public sealed class NvmlGpuClocks : IGpuClocks
{
    public int? PowerWatts() => Nvml.PowerUsageWatts();
    public int? GraphicsClockMhz() => Nvml.GraphicsClockMhz();
    public int? MaxClockMhz() => Nvml.MaxGraphicsClockMhz();

    public void Lock(int maxMhz)
    {
        if (Nvml.LockGraphicsClock(GpuLimiter.MinGpuMhz, maxMhz) is not 0 and var code)
            throw new PowerException($"The driver refused the GPU clock lock (NVML {code?.ToString() ?? "unavailable"}).");
    }

    public void Unlock() => Nvml.ResetGraphicsClock();
}

/// <summary>Reads the GPU power limit the driver enforces and the power drawn now; every failure is "unknown" (null).</summary>
public static class Nvml
{
    private static readonly object Gate = new();
    private static bool _tried, _ready;
    private static IntPtr _device;

    public static int? EnforcedPowerLimitWatts() => Milliwatts(GetEnforcedPowerLimit);

    public static int? PowerUsageWatts() => Milliwatts(GetPowerUsage);

    /// <summary>The GPU die temperature in °C; null when the driver cannot tell (no NVIDIA GPU, or it is switched off).</summary>
    public static int? TemperatureC() => Query((IntPtr device, out uint c) => GetTemperature(device, 0, out c));

    private const int GraphicsClock = 0;

    public static int? GraphicsClockMhz() => Query((IntPtr device, out uint mhz) => GetClockInfo(device, GraphicsClock, out mhz));

    public static int? MaxGraphicsClockMhz() => Query((IntPtr device, out uint mhz) => GetMaxClockInfo(device, GraphicsClock, out mhz));

    /// <summary>NVML's return code (0 = done), null when NVML is not there. Needs administrator/root rights (the service has them).</summary>
    public static int? LockGraphicsClock(int minMhz, int maxMhz) =>
        Run(device => SetGpuLockedClocks(device, (uint)minMhz, (uint)maxMhz));

    public static int? ResetGraphicsClock() => Run(ResetGpuLockedClocks);

    private delegate int NvmlQuery(IntPtr device, out uint value);

    private static int? Run(Func<IntPtr, int> call)
    {
        int code = -1;
        return Query((IntPtr device, out uint value) => { code = call(device); value = 0; return 0; }) is null ? null : code;
    }

    private static int? Milliwatts(NvmlQuery query) => Query(query) is { } mw ? (int)Math.Round(mw / 1000.0) : null;

    private static int? Query(NvmlQuery query)
    {
        lock (Gate)
        {
            if (!_tried)
            {
                _tried = true;
                try
                {
                    _ready = Init() == 0 && GetHandleByIndex(0, out _device) == 0;
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
            }
            if (!_ready) return null;
            try
            {
                return query(_device, out uint value) == 0 ? (int)value : null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
        }
    }

    private static int Init() => OperatingSystem.IsLinux() ? Lin.nvmlInit_v2() : Win.nvmlInit_v2();
    private static int GetHandleByIndex(uint index, out IntPtr device) => OperatingSystem.IsLinux() ? Lin.nvmlDeviceGetHandleByIndex_v2(index, out device) : Win.nvmlDeviceGetHandleByIndex_v2(index, out device);
    private static int GetEnforcedPowerLimit(IntPtr device, out uint mw) => OperatingSystem.IsLinux() ? Lin.nvmlDeviceGetEnforcedPowerLimit(device, out mw) : Win.nvmlDeviceGetEnforcedPowerLimit(device, out mw);
    private static int GetPowerUsage(IntPtr device, out uint mw) => OperatingSystem.IsLinux() ? Lin.nvmlDeviceGetPowerUsage(device, out mw) : Win.nvmlDeviceGetPowerUsage(device, out mw);
    private static int GetTemperature(IntPtr device, int sensor, out uint c) => OperatingSystem.IsLinux() ? Lin.nvmlDeviceGetTemperature(device, sensor, out c) : Win.nvmlDeviceGetTemperature(device, sensor, out c);
    private static int GetClockInfo(IntPtr device, int type, out uint mhz) => OperatingSystem.IsLinux() ? Lin.nvmlDeviceGetClockInfo(device, type, out mhz) : Win.nvmlDeviceGetClockInfo(device, type, out mhz);
    private static int GetMaxClockInfo(IntPtr device, int type, out uint mhz) => OperatingSystem.IsLinux() ? Lin.nvmlDeviceGetMaxClockInfo(device, type, out mhz) : Win.nvmlDeviceGetMaxClockInfo(device, type, out mhz);
    private static int SetGpuLockedClocks(IntPtr device, uint minMhz, uint maxMhz) => OperatingSystem.IsLinux() ? Lin.nvmlDeviceSetGpuLockedClocks(device, minMhz, maxMhz) : Win.nvmlDeviceSetGpuLockedClocks(device, minMhz, maxMhz);
    private static int ResetGpuLockedClocks(IntPtr device) => OperatingSystem.IsLinux() ? Lin.nvmlDeviceResetGpuLockedClocks(device) : Win.nvmlDeviceResetGpuLockedClocks(device);

    private static class Win
    {
        [DllImport("nvml.dll")] public static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceSetGpuLockedClocks(IntPtr device, uint minMhz, uint maxMhz);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceResetGpuLockedClocks(IntPtr device);
    }

    private static class Lin
    {
        [DllImport("libnvidia-ml.so.1")] public static extern int nvmlInit_v2();
        [DllImport("libnvidia-ml.so.1")] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("libnvidia-ml.so.1")] public static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
        [DllImport("libnvidia-ml.so.1")] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport("libnvidia-ml.so.1")] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);
        [DllImport("libnvidia-ml.so.1")] public static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport("libnvidia-ml.so.1")] public static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport("libnvidia-ml.so.1")] public static extern int nvmlDeviceSetGpuLockedClocks(IntPtr device, uint minMhz, uint maxMhz);
        [DllImport("libnvidia-ml.so.1")] public static extern int nvmlDeviceResetGpuLockedClocks(IntPtr device);
    }
}
