using System.Management;
using System.Runtime.InteropServices;

namespace Excalibur.App;

/// <summary>
/// Reads and sets the refresh rate of the laptop's own screen (docs/08 3.1: charger → the fast
/// rate, battery → 60 Hz). Runs in the user's session (the tray); needs no administrator rights.
/// The internal panel is found through WMI (video output technology "internal"), so an external
/// monitor is never touched. Only rates the display driver lists for the current resolution are used.
/// </summary>
internal static class DisplayRefresh
{
    public sealed record Panel(string DeviceName, int CurrentHz, IReadOnlyList<int> SupportedHz);

    /// <summary>The internal panel, or null when there is none (a desktop, a closed lid with an external monitor).</summary>
    public static Panel? FindInternal()
    {
        var internalCodes = InternalMonitorCodes();
        for (uint i = 0; ; i++)
        {
            var adapter = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
            if ((adapter.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;

            var monitor = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0)) continue;
            // DeviceID looks like MONITOR\CSO1234\{guid}\0001; WMI names the same monitor DISPLAY\CSO1234\...
            string[] parts = monitor.DeviceID.Split('\\');
            if (parts.Length < 2 || !internalCodes.Contains(parts[1], StringComparer.OrdinalIgnoreCase)) continue;

            var current = NewMode();
            if (!EnumDisplaySettings(adapter.DeviceName, EnumCurrentSettings, ref current)) continue;
            return new Panel(adapter.DeviceName, (int)current.dmDisplayFrequency, SupportedAt(adapter.DeviceName, current));
        }
        return null;
    }

    /// <summary>Sets the rate (written to the registry, like the Windows settings page); false when the driver refused.</summary>
    public static bool TrySet(Panel panel, int hz)
    {
        if (panel.CurrentHz == hz) return true;
        if (!panel.SupportedHz.Contains(hz)) return false;
        var mode = NewMode();
        if (!EnumDisplaySettings(panel.DeviceName, EnumCurrentSettings, ref mode)) return false;
        mode.dmDisplayFrequency = (uint)hz;
        mode.dmFields = DmDisplayFrequency;
        return ChangeDisplaySettingsEx(panel.DeviceName, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero) == DispChangeSuccessful;
    }

    private static List<int> SupportedAt(string device, DevMode current)
    {
        var rates = new SortedSet<int>();
        var mode = NewMode();
        for (int i = 0; EnumDisplaySettings(device, i, ref mode); i++)
            if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight
                && mode.dmBitsPerPel == current.dmBitsPerPel && mode.dmDisplayFrequency > 1)
                rates.Add((int)mode.dmDisplayFrequency);
        return [.. rates];
    }

    /// <summary>The EDID manufacturer+product codes of the monitors wired as the laptop's own screen.</summary>
    private static List<string> InternalMonitorCodes()
    {
        var codes = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams");
            foreach (ManagementObject m in searcher.Get())
            {
                using (m)
                {
                    // 0x80000000 = D3DKMDT_VOT_INTERNAL; the property comes back as a signed int.
                    if (Convert.ToInt64(m["VideoOutputTechnology"]) is not (0x80000000L or -2147483648L)) continue;
                    string[] parts = ((string)m["InstanceName"]).Split('\\');
                    if (parts.Length >= 2) codes.Add(parts[1]);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or InvalidCastException) { }
        return codes;
    }

    private static DevMode NewMode() => new() { dmDeviceName = new string('\0', 32), dmFormName = new string('\0', 32), dmSize = (ushort)Marshal.SizeOf<DevMode>() };

    private const int EnumCurrentSettings = -1, DispChangeSuccessful = 0;
    private const uint DisplayDeviceAttachedToDesktop = 1, CdsUpdateRegistry = 1, DmDisplayFrequency = 0x400000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? device, int modeNum, ref DevMode mode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? device, ref DevMode mode, IntPtr hwnd, uint flags, IntPtr param);
}
