using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Excalibur.App;

/// <summary>
/// Finds and starts the app's two processes (tray and main window) without referencing either
/// UI framework, so both modes can call it.
/// </summary>
internal static class AppInstances
{
    public static string TrayMutex(bool sim) => sim ? @"Local\ExcaliburReimaginedTraySim" : @"Local\ExcaliburReimaginedTray";
    public static string WindowMutex(bool sim) => sim ? @"Local\ExcaliburReimaginedWindowSim" : @"Local\ExcaliburReimaginedWindow";

    /// <summary>Brings the open main window to the front, or starts the window process.</summary>
    public static void OpenMainWindow(bool sim)
    {
        if (ActivateMainWindow()) return;
        Start("--window" + (sim ? " --sim" : ""));
    }

    /// <summary>The main window started from the Start menu also brings back the tray if it is not running.</summary>
    public static void EnsureTray(bool sim)
    {
        if (Mutex.TryOpenExisting(TrayMutex(sim), out var existing))
        {
            existing.Dispose();
            return;
        }
        Start(sim ? "--sim" : "");
    }

    /// <summary>Restores and focuses another instance's main window; false when there is none.</summary>
    public static bool ActivateMainWindow()
    {
        using var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(p.MainWindowHandle);
                return true;
            }
        }
        return false;
    }

    /// <summary>Asks another instance's main window to close (as the close button would); used when the tray exits.</summary>
    public static void CloseMainWindow()
    {
        using var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                if (p.Id == me.Id) continue;
                try
                {
                    // Inside the try: a process that exits meanwhile throws on MainWindowHandle too.
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    p.CloseMainWindow();
                    p.WaitForExit(2000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    private static void Start(string arguments)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
    }

    private const int SW_RESTORE = 9;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
}
