using System.Runtime.InteropServices;
using Excalibur.Core.Fan.Ipc;

namespace Excalibur.App.Tray;

/// <summary>
/// Keyboard light idle-off (docs/08 4.3): the service runs in session 0 and cannot see the user's input, so the tray
/// compares the time since the last keyboard or mouse input with the chosen minutes on every poll and tells the service
/// when that differs from what it shows. Comparing with the service's state (not remembering what was sent) also
/// covers a service that restarted.
/// </summary>
internal static class LightIdle
{
    public static async Task SyncAsync(FanStatusDto s, FanPipeClient client)
    {
        bool idle = s.LightIdleOffMinutes > 0 && SinceLastInput() >= TimeSpan.FromMinutes(s.LightIdleOffMinutes);
        if (idle == s.LightIdle) return;
        await client.SendAsync(new FanRequest("setLightIdle", Idle: idle, Source: FanSource.Tray));
    }

    private static TimeSpan SinceLastInput()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
