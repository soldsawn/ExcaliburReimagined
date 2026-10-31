using System.IO.Compression;
using System.Text;

namespace Excalibur.Core.Settings;

/// <summary>
/// A problem report the user sends by hand (nothing is uploaded): one zip with the last days' service and guardian logs,
/// the small state files (EC version, settings, measured RPM scale and power modes), the tray's log and a summary, and a
/// GitHub issue link with the summary filled in. The user's name and profile folder are replaced in every text file.
/// </summary>
public static class BugReport
{
    public const string IssuesUrl = "https://github.com/soldsawn/ExcaliburReimagined/issues/new";
    public const int Days = 3;
    // Small state files worth seeing; the history (large, binary) and battery logs stay out.
    private static readonly string[] StateFiles = ["ec-version.txt", "fan.json", "fan-rpm-scale.txt", "power-modes.txt"];

    /// <param name="dataDirectory">The service's data folder (logs\ and the state files).</param>
    /// <param name="extra">Other files to add as (path, name in the zip): the tray log, the user settings.</param>
    /// <returns>The zip's path.</returns>
    public static string Create(string outputDirectory, string dataDirectory, string summary, IEnumerable<(string Path, string Name)> extra, DateTime now)
    {
        Directory.CreateDirectory(outputDirectory);
        string zipPath = Path.Combine(outputDirectory, $"ExcaliburReimagined-rapor-{now:yyyyMMdd-HHmm}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        Add(zip, "bilgi.txt", Clean(
            $"{summary}\nİşletim sistemi: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\nRapor: {now:yyyy-MM-dd HH:mm}\n"));
        string logs = Path.Combine(dataDirectory, "logs");
        if (Directory.Exists(logs))
            foreach (var file in Directory.GetFiles(logs).Where(f => File.GetLastWriteTime(f) >= now.AddDays(-Days)).Order())
                AddFile(zip, file, $"logs/{Path.GetFileName(file)}");
        foreach (string name in StateFiles)
            AddFile(zip, Path.Combine(dataDirectory, name), name);
        foreach (var (path, name) in extra)
            AddFile(zip, path, name);
        return zipPath;
    }

    /// <summary>A new-issue link with the title and the summary filled in (the zip is attached by hand).</summary>
    public static string IssueLink(string summary) =>
        $"{IssuesUrl}?title={Uri.EscapeDataString("Sorun: ")}&body={Uri.EscapeDataString(
            $"**Ne oldu?**\n\n\n**Ne bekliyordunuz?**\n\n\n**Bilgiler:** {summary}\n\n" +
            "Masaüstündeki ExcaliburReimagined-rapor zip dosyasını bu kutuya sürükleyip bırakın (günlükler içinde).")}";

    /// <summary>The user's profile folder and name replaced, so paths in logs and settings do not carry them.</summary>
    public static string Clean(string text)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) text = text.Replace(profile, "<kullanıcı klasörü>", StringComparison.OrdinalIgnoreCase);
        string user = Environment.UserName;
        if (user.Length >= 3) text = text.Replace(user, "<kullanıcı>", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static void AddFile(ZipArchive zip, string path, string name)
    {
        if (!File.Exists(path)) return;
        string text;
        try
        {
            // Logs are being written by the service: share the file.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = $"(okunamadı: {ex.Message})"; }
        Add(zip, name, Clean(text));
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
