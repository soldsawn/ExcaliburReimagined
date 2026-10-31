using System.IO.Compression;
using Excalibur.Core.Settings;

namespace Excalibur.Core.Tests;

public class BugReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"report-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void The_zip_has_recent_logs_state_files_and_a_summary_without_the_user_name()
    {
        var now = new DateTime(2026, 11, 1, 12, 0, 0);
        string data = Path.Combine(_root, "data"), logs = Path.Combine(data, "logs");
        Directory.CreateDirectory(logs);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        File.WriteAllText(Path.Combine(logs, "fan-20261101.log"), $"started; rule for {profile}\\Games\\game.exe");
        File.WriteAllText(Path.Combine(logs, "fan-20261020.log"), "old");
        File.SetLastWriteTime(Path.Combine(logs, "fan-20261101.log"), now.AddHours(-1));
        File.SetLastWriteTime(Path.Combine(logs, "fan-20261020.log"), now.AddDays(-12));
        File.WriteAllText(Path.Combine(data, "ec-version.txt"), "CQ212 P009B0");
        File.WriteAllBytes(Path.Combine(data, "heartbeat.bin"), [1, 2, 3]);
        string tray = Path.Combine(_root, "tray.log");
        File.WriteAllText(tray, "tray: started");

        string zipPath = BugReport.Create(Path.Combine(_root, "out"), data, "Excalibur Reimagined 0.1.9 · EC P009B0", [(tray, "tray.log")], now);

        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).Order().ToList();
        Assert.Equal(["bilgi.txt", "ec-version.txt", "logs/fan-20261101.log", "tray.log"], names);
        string log = new StreamReader(zip.GetEntry("logs/fan-20261101.log")!.Open()).ReadToEnd();
        Assert.DoesNotContain(profile, log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<kullanıcı klasörü>", log);
        Assert.Contains("EC P009B0", new StreamReader(zip.GetEntry("bilgi.txt")!.Open()).ReadToEnd());
    }

    [Fact]
    public void The_issue_link_carries_the_summary()
    {
        string link = BugReport.IssueLink("EC P009B0");
        Assert.StartsWith(BugReport.IssuesUrl + "?title=", link);
        Assert.Contains(Uri.EscapeDataString("EC P009B0"), link);
    }
}
