namespace Excalibur.App;

/// <summary>
/// A small log of the tray's exit path (menu opened, item clicked, each exit step), in
/// %LOCALAPPDATA%\ExcaliburReimagined\tray.log, for diagnosing an Exit that needs two clicks. Never throws.
/// </summary>
internal static class TrayLog
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExcaliburReimagined", "tray.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
