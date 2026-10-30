using System.Windows;
using Excalibur.Core.Settings;

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
        using var settings = new UserSettingsStore();
        app.Resources.MergedDictionaries.Add(Palette.Build(settings.Current));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ExcaliburReimagined;component/Ui/Styles.xaml"),
        });

        var connection = new FanConnection(sim);
        var window = new MainWindow(connection);
        // Settings' Appearance card saves the file; every change shows at once (the palette is the first dictionary).
        settings.Changed += s => app.Dispatcher.BeginInvoke(() =>
        {
            app.Resources.MergedDictionaries[0] = Palette.Build(s);
            window.ApplyAppearance();
        });
        if (snapshot is not null) window.SnapshotAndClose(snapshot);
        app.Run(window);
        connection.Dispose();
    }
}
