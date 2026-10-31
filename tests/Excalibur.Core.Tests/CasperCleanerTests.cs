using Excalibur.Core.Casper;

namespace Excalibur.Core.Tests;

public class CasperCleanerTests : IDisposable
{
    private sealed class FakeSystem : ICasperSystem
    {
        public Dictionary<string, ServiceEntry> Services = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TaskEntry> Tasks = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Processes = [];
        public string? FailStop;

        IReadOnlyList<ServiceEntry> ICasperSystem.Services() => [.. Services.Values];
        public void SetStart(string service, int start) => Services[service] = Services[service] with { Start = start };
        public void Stop(string service)
        {
            if (service == FailStop) throw new InvalidOperationException("access denied");
            Services[service] = Services[service] with { Running = false };
        }
        public void StartService(string service) => Services[service] = Services[service] with { Running = true };
        IReadOnlyList<TaskEntry> ICasperSystem.Tasks() => [.. Tasks.Values];
        public void SetTaskEnabled(string path, bool enabled) => Tasks[path] = Tasks[path] with { Enabled = enabled };
        public IReadOnlyList<string> RunningProcesses() => [.. Processes];
        public void Kill(string processName) => Processes.Remove(processName);
    }

    private readonly string _state = Path.Combine(Path.GetTempPath(), $"casper-{Guid.NewGuid():N}.json");
    private readonly FakeSystem _sys = new();

    public CasperCleanerTests()
    {
        _sys.Services["ControlCenter"] = new("ControlCenter", "ControlCenter", @"\??\C:\Program Files (x86)\CASPER EXCALIBUR\ControlCenter64.sys", 2, true);
        _sys.Services["NbfcService"] = new("NbfcService", "NoteBook FanControl", @"C:\Program Files\NBFC\NbfcService.exe", 4, false);
        _sys.Services["ExcaliburFan"] = new("ExcaliburFan", "Excalibur Fan", @"C:\Program Files\ExcaliburReimagined\ExcaliburFanService.exe", 2, true);
        _sys.Services["I3DHotkey"] = new("I3DHotkey", "Hotkey OSD Driver", @"C:\Windows\System32\drivers\HotkeyOSD.sys", 2, true);
        _sys.Tasks[@"\ControlCenter\ControlCenter"] = new(@"\ControlCenter\ControlCenter", null, [@"C:\Program Files (x86)\CASPER EXCALIBUR\ControlCenterDaemon.exe"], true);
        _sys.Tasks[@"\Quanta\Radio"] = new(@"\Quanta\Radio", "Quanta task service", [@"C:\Windows\WindowsRadioCheck.exe"], true);
        _sys.Tasks[@"\Microsoft\Windows\Defrag\ScheduledDefrag"] = new(@"\Microsoft\Windows\Defrag\ScheduledDefrag", "Optimizes drives", [@"%windir%\system32\defrag.exe"], true);
        _sys.Processes = ["controlcenterdaemon.exe", "explorer.exe"];
    }

    public void Dispose() { if (File.Exists(_state)) File.Delete(_state); }

    [Fact]
    public void Casper_parts_are_found_and_other_fan_tools_only_reported()
    {
        var r = new CasperCleaner(_sys, _state).Scan();
        Assert.True(r.HasCasper);
        Assert.False(r.CanRestore);
        Assert.Equal(["ControlCenter"], r.Items.Where(i => i.Kind == CasperKind.Service).Select(i => i.Id));
        Assert.Equal(["NbfcService"], r.Items.Where(i => i.Kind == CasperKind.Warning).Select(i => i.Id));
        Assert.Equal([@"\ControlCenter\ControlCenter", @"\Quanta\Radio"], r.Items.Where(i => i.Kind == CasperKind.Task).Select(i => i.Id).Order());
        Assert.Equal(["controlcenterdaemon.exe"], r.Items.Where(i => i.Kind == CasperKind.Process).Select(i => i.Id));
    }

    [Fact]
    public void Disable_then_restore_puts_everything_back()
    {
        var cleaner = new CasperCleaner(_sys, _state);
        var off = cleaner.Disable();
        Assert.Null(off.Error);
        Assert.True(off.CanRestore);
        Assert.Equal(4, _sys.Services["ControlCenter"].Start);
        Assert.False(_sys.Services["ControlCenter"].Running);
        Assert.False(_sys.Tasks[@"\ControlCenter\ControlCenter"].Enabled);
        Assert.False(_sys.Tasks[@"\Quanta\Radio"].Enabled);
        Assert.DoesNotContain("controlcenterdaemon.exe", _sys.Processes);
        // Not touched: our own service, the Fn key display, other tools, Windows' tasks.
        Assert.True(_sys.Services["ExcaliburFan"].Running);
        Assert.True(_sys.Services["I3DHotkey"].Running);
        Assert.Equal(4, _sys.Services["NbfcService"].Start);
        Assert.True(_sys.Tasks[@"\Microsoft\Windows\Defrag\ScheduledDefrag"].Enabled);

        var back = cleaner.Restore();
        Assert.Null(back.Error);
        Assert.False(back.CanRestore);
        Assert.Equal(2, _sys.Services["ControlCenter"].Start);
        Assert.True(_sys.Services["ControlCenter"].Running);
        Assert.True(_sys.Tasks[@"\ControlCenter\ControlCenter"].Enabled);
        Assert.True(_sys.Tasks[@"\Quanta\Radio"].Enabled);
    }

    [Fact]
    public void A_second_disable_keeps_the_first_state()
    {
        var cleaner = new CasperCleaner(_sys, _state);
        cleaner.Disable();
        cleaner.Disable(); // everything already off now: must not be remembered as "was off"
        cleaner.Restore();
        Assert.Equal(2, _sys.Services["ControlCenter"].Start);
        Assert.True(_sys.Tasks[@"\Quanta\Radio"].Enabled);
    }

    [Fact]
    public void A_failure_is_reported_and_the_state_kept_for_undo()
    {
        _sys.FailStop = "ControlCenter";
        var r = new CasperCleaner(_sys, _state).Disable();
        Assert.Contains("access denied", r.Error);
        Assert.True(r.CanRestore);
        Assert.Equal(4, _sys.Services["ControlCenter"].Start);
    }

    [Fact]
    public void Nothing_to_do_without_Casper()
    {
        _sys.Services.Remove("ControlCenter");
        _sys.Tasks.Remove(@"\ControlCenter\ControlCenter");
        _sys.Tasks.Remove(@"\Quanta\Radio");
        _sys.Processes.Clear();
        var r = new CasperCleaner(_sys, _state).Scan();
        Assert.False(r.HasCasper);
        Assert.Single(r.Items); // the NBFC warning
    }
}
