using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Excalibur.App.Tray;

namespace Excalibur.App;

internal static class Program
{
    /// <summary>
    /// Excalibur Reimagined, the replacement for the Casper Control Center. One exe, two modes,
    /// each in its own process so the part that runs all day stays small:
    ///   (default)           tray mode: icon, menu, quick panel, OSD (WinForms; WPF never loads)
    ///   --window            the main window (WPF); closing it ends that process entirely
    ///   --sim               connect to the --simulate fan service
    ///   --snapshot <png>    (development) save images of the UI to PNG files, then exit
    /// Neither mode touches the EC: everything goes through the fan service's pipe.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        bool sim = Has(args, "--sim");
        int snap = Array.FindIndex(args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        string? snapshot = snap >= 0 && snap + 1 < args.Length ? args[snap + 1] : null;

        // Development: list the screens and their refresh rates (reads only), then exit.
        int dp = Array.FindIndex(args, a => a.Equals("--displays", StringComparison.OrdinalIgnoreCase));
        if (dp >= 0 && dp + 1 < args.Length)
        {
            var panel = DisplayRefresh.FindInternal();
            File.WriteAllText(args[dp + 1], panel is null ? "no internal panel found"
                : $"{panel.DeviceName}: now {panel.CurrentHz} Hz, supported {string.Join(", ", panel.SupportedHz)} Hz");
            return;
        }

        if (Has(args, "--window"))
        {
            RunWindow(sim, snapshot);
            return;
        }

        // A snapshot run exits by itself, so it may run beside the installed tray.
        using var single = new Mutex(true, AppInstances.TrayMutex(sim), out bool first);
        if (!first && snapshot is null) return;

        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.Run(new TrayContext(sim, snapshot));
    }

    /// <summary>Kept out of <see cref="Main"/> so tray mode never JIT-compiles anything that loads WPF.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunWindow(bool sim, string? snapshot) => Ui.MainWindowHost.Run(sim, snapshot);

    private static bool Has(string[] args, string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);
}
