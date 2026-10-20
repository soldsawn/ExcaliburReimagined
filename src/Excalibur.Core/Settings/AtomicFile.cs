namespace Excalibur.Core.Settings;

/// <summary>
/// Writes a whole file so a crash or power cut leaves either the old or the new content,
/// never half of it: write a temp file next to it, then swap it in.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        if (File.Exists(path))
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(temp, path);
    }
}
