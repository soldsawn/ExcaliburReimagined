using System.Text.Json;
using System.Text.Json.Serialization;

namespace Excalibur.Core.Casper;

/// <summary>What a found component is.</summary>
public enum CasperKind { Service, Task, Process, Warning }

/// <summary>
/// One component the cleaner found. <see cref="Disabled"/> is its state now (a service set to Disabled, a task turned off,
/// a process not running); a <see cref="CasperKind.Warning"/> is another fan tool that also talks to the EC: shown, never touched.
/// </summary>
public sealed record CasperItem(CasperKind Kind, string Id, string Name, string Detail, bool Disabled);

/// <summary>What the Settings card shows: the components, and whether a restore is possible (something was turned off by us).</summary>
public sealed record CasperReport(IReadOnlyList<CasperItem> Items, bool CanRestore, string? Error = null)
{
    public bool HasCasper => Items.Any(i => i.Kind != CasperKind.Warning);
}

/// <summary>A Windows service or driver as the cleaner sees it. <see cref="Start"/> is the registry Start value (2 auto, 3 manual, 4 disabled).</summary>
public sealed record ServiceEntry(string Name, string DisplayName, string? ImagePath, int Start, bool Running);

/// <summary>A scheduled task: its full path, description and the programs it starts.</summary>
public sealed record TaskEntry(string Path, string? Description, IReadOnlyList<string> Programs, bool Enabled);

/// <summary>The few system calls the cleaner needs; the service implements them for real, tests with a fake.</summary>
public interface ICasperSystem
{
    IReadOnlyList<ServiceEntry> Services();
    void SetStart(string service, int start);
    void Stop(string service);
    void StartService(string service);
    IReadOnlyList<TaskEntry> Tasks();
    void SetTaskEnabled(string path, bool enabled);
    /// <summary>Running processes by file name (lower case, with .exe).</summary>
    IReadOnlyList<string> RunningProcesses();
    void Kill(string processName);
}

/// <summary>
/// Turns Casper's Control Center parts off and back on (A8, docs/03 D1-D3): the WinRing0-like ControlCenter driver service,
/// the ControlCenter scheduled task (ControlCenterDaemon.exe at logon), Quanta's CTS task service, and their running
/// processes. Only stops and disables, never deletes; the original start types and task states go to a file so
/// <see cref="Restore"/> puts them back. Other EC tools (NoteBook FanControl, WinRing0 drivers) are only reported.
/// Components are recognised by their program paths and descriptions, not by a fixed name list.
/// </summary>
public sealed class CasperCleaner(ICasperSystem system, string statePath)
{
    private static readonly string[] CasperPrograms = ["controlcenter.exe", "controlcenterdaemon.exe", "cts.exe"];

    private sealed record SavedService(string Name, int Start, bool Running);
    private sealed record SavedTask(string Path, bool Enabled);
    private sealed record State(List<SavedService> Services, List<SavedTask> Tasks);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static bool IsCasperService(ServiceEntry s) =>
        !s.Name.StartsWith("Excalibur", StringComparison.OrdinalIgnoreCase)
        && (s.ImagePath?.Contains("ControlCenter", StringComparison.OrdinalIgnoreCase) == true
            || s.ImagePath?.Contains(@"\CASPER", StringComparison.OrdinalIgnoreCase) == true
            || CasperPrograms.Any(p => s.ImagePath?.Contains(p, StringComparison.OrdinalIgnoreCase) == true));

    /// <summary>Another fan tool's service or a WinRing0 driver (not Casper's own copy): it also talks to the EC.</summary>
    public static bool IsOtherEcTool(ServiceEntry s) =>
        !IsCasperService(s)
        && (s.Name.Equals("NbfcService", StringComparison.OrdinalIgnoreCase)
            || s.ImagePath?.Contains("WinRing0", StringComparison.OrdinalIgnoreCase) == true
            || s.Name.StartsWith("WinRing0", StringComparison.OrdinalIgnoreCase));

    public static bool IsCasperTask(TaskEntry t) =>
        t.Path.StartsWith(@"\ControlCenter\", StringComparison.OrdinalIgnoreCase)
        || t.Description?.Contains("Quanta task service", StringComparison.OrdinalIgnoreCase) == true
        || t.Programs.Any(p => CasperPrograms.Contains(System.IO.Path.GetFileName(p).Trim('"').ToLowerInvariant()));

    public CasperReport Scan()
    {
        var items = new List<CasperItem>();
        foreach (var s in system.Services())
        {
            if (IsCasperService(s))
                items.Add(new CasperItem(CasperKind.Service, s.Name, s.DisplayName, s.ImagePath ?? "", s.Start == 4 && !s.Running));
            else if (IsOtherEcTool(s))
                items.Add(new CasperItem(CasperKind.Warning, s.Name, s.DisplayName, s.ImagePath ?? "", s.Start == 4 && !s.Running));
        }
        foreach (var t in system.Tasks().Where(IsCasperTask))
            items.Add(new CasperItem(CasperKind.Task, t.Path, System.IO.Path.GetFileName(t.Path), string.Join(", ", t.Programs), !t.Enabled));
        var running = system.RunningProcesses();
        foreach (var p in CasperPrograms.Where(running.Contains))
            items.Add(new CasperItem(CasperKind.Process, p, p, "çalışıyor", false));
        return new CasperReport(items, File.Exists(statePath));
    }

    /// <summary>Stops and disables every Casper component found, remembering how each was (the first run's state is kept).</summary>
    public CasperReport Disable()
    {
        var state = Load() ?? new State([], []);
        foreach (var s in system.Services().Where(IsCasperService))
        {
            if (!state.Services.Any(x => x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)))
                state.Services.Add(new SavedService(s.Name, s.Start, s.Running));
        }
        foreach (var t in system.Tasks().Where(IsCasperTask))
        {
            if (!state.Tasks.Any(x => x.Path.Equals(t.Path, StringComparison.OrdinalIgnoreCase)))
                state.Tasks.Add(new SavedTask(t.Path, t.Enabled));
        }
        // Written before anything changes, so a failure half way can still be undone.
        Save(state);
        var errors = new List<string>();
        foreach (var t in state.Tasks) Try(errors, $"görev {t.Path}", () => system.SetTaskEnabled(t.Path, false));
        foreach (var p in CasperPrograms) Try(errors, p, () => { if (system.RunningProcesses().Contains(p)) system.Kill(p); });
        foreach (var s in state.Services)
        {
            Try(errors, s.Name, () => system.SetStart(s.Name, 4));
            Try(errors, s.Name, () => system.Stop(s.Name));
        }
        return Scan() with { Error = errors.Count > 0 ? string.Join("; ", errors) : null };
    }

    /// <summary>Puts every remembered component back as it was, then forgets the state.</summary>
    public CasperReport Restore()
    {
        if (Load() is not { } state) return Scan();
        var errors = new List<string>();
        foreach (var s in state.Services)
        {
            Try(errors, s.Name, () => system.SetStart(s.Name, s.Start));
            if (s.Running) Try(errors, s.Name, () => system.StartService(s.Name));
        }
        foreach (var t in state.Tasks) Try(errors, $"görev {t.Path}", () => system.SetTaskEnabled(t.Path, t.Enabled));
        if (errors.Count == 0) File.Delete(statePath);
        return Scan() with { Error = errors.Count > 0 ? string.Join("; ", errors) : null };
    }

    private static void Try(List<string> errors, string what, Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add($"{what}: {ex.Message}"); }
    }

    private State? Load()
    {
        try { return File.Exists(statePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(statePath), Json) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private void Save(State state)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, Json));
    }
}

/// <summary>A Control Center installation in memory, for the --simulate service (nothing on the real system is touched).</summary>
public sealed class SimulatedCasperSystem : ICasperSystem
{
    private readonly Dictionary<string, ServiceEntry> _services = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ControlCenter"] = new("ControlCenter", "ControlCenter", @"\??\C:\Program Files (x86)\CASPER EXCALIBUR\ControlCenter64.sys", 2, true),
        ["NbfcService"] = new("NbfcService", "NoteBook FanControl Service", @"C:\Program Files (x86)\NoteBook FanControl\NbfcService.exe", 4, false),
    };
    private readonly Dictionary<string, TaskEntry> _tasks = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"\ControlCenter\ControlCenter"] = new(@"\ControlCenter\ControlCenter", null, [@"C:\Program Files (x86)\CASPER EXCALIBUR\ControlCenterDaemon.exe"], true),
        [@"\Quanta\Windows radio check service"] = new(@"\Quanta\Windows radio check service", "Quanta task service", [@"C:\Program Files (x86)\Quanta\WindowsRadioCheck.exe"], true),
    };
    private readonly HashSet<string> _processes = ["controlcenterdaemon.exe"];
    private readonly object _gate = new();

    public IReadOnlyList<ServiceEntry> Services() { lock (_gate) return [.. _services.Values]; }
    public void SetStart(string service, int start) { lock (_gate) _services[service] = _services[service] with { Start = start }; }
    public void Stop(string service) { lock (_gate) _services[service] = _services[service] with { Running = false }; }
    public void StartService(string service) { lock (_gate) _services[service] = _services[service] with { Running = true }; }
    public IReadOnlyList<TaskEntry> Tasks() { lock (_gate) return [.. _tasks.Values]; }
    public void SetTaskEnabled(string path, bool enabled) { lock (_gate) _tasks[path] = _tasks[path] with { Enabled = enabled }; }
    public IReadOnlyList<string> RunningProcesses() { lock (_gate) return [.. _processes]; }
    public void Kill(string processName) { lock (_gate) _processes.Remove(processName); }
}
