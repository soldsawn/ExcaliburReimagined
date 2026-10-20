using System.Windows;

namespace Excalibur.App.Ui;

/// <summary>Runs the main window process (<c>--window</c>): one at a time, theme loaded, tray ensured.</summary>
internal static class MainWindowHost
{
    public static void Run(bool sim, string? snapshot)
    {
        using var single = new Mutex(true, AppInstances.WindowMutex(sim), out bool first);
        if (!first && snapshot is null)
        {
            AppInstances.ActivateMainWindow();
            return;
        }
        if (snapshot is null)
        {
            try { AppInstances.EnsureTray(sim); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(Palette.Build(Palette.SystemIsDark()));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ExcaliburReimagined;component/Ui/Styles.xaml"),
        });

        var connection = new FanConnection(sim);
        var window = new MainWindow(connection);
        if (snapshot is not null) window.SnapshotAndClose(snapshot);
        app.Run(window);
        connection.Dispose();
    }
}
