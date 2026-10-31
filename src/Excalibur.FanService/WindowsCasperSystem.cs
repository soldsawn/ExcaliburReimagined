using System.Diagnostics;
using System.ServiceProcess;
using Excalibur.Core.Casper;
using Microsoft.Win32;

namespace Excalibur.FanService;

/// <summary>
/// The cleaner's view of this Windows: services and drivers from the registry (HKLM\SYSTEM\CurrentControlSet\Services: the
/// driver services do not show in the service list), scheduled tasks from the Task Scheduler, processes from the process list.
/// Runs inside the fan service (SYSTEM), which may change start types and tasks.
/// </summary>
internal sealed class WindowsCasperSystem : ICasperSystem
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    public IReadOnlyList<ServiceEntry> Services()
    {
        var list = new List<ServiceEntry>();
        using var root = Registry.LocalMachine.OpenSubKey(ServicesKey);
        if (root is null) return list;
        foreach (string name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("ImagePath") is not string image || key.GetValue("Start") is not int start) continue;
            string display = key.GetValue("DisplayName") as string ?? name;
            if (display.StartsWith('@')) display = name; // a resource reference (Windows' own services)
            list.Add(new ServiceEntry(name, display, image, start, IsRunning(name)));
        }
        return list;
    }

    private static bool IsRunning(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status is not ServiceControllerStatus.Stopped;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return false; }
    }

    public void SetStart(string service, int start)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{service}", writable: true)
            ?? throw new InvalidOperationException($"{service} is not installed.");
        key.SetValue("Start", start, RegistryValueKind.DWord);
    }

    public void Stop(string service)
    {
        using var sc = new ServiceController(service);
        if (sc.Status is ServiceControllerStatus.Stopped) return;
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, Wait);
    }

    public void StartService(string service)
    {
        using var sc = new ServiceController(service);
        if (sc.Status is ServiceControllerStatus.Running) return;
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, Wait);
    }

    public IReadOnlyList<TaskEntry> Tasks()
    {
        var list = new List<TaskEntry>();
        dynamic? scheduler = Scheduler();
        if (scheduler is null) return list;
        Walk(scheduler.GetFolder(@"\"), list);
        return list;
    }

    private static void Walk(dynamic folder, List<TaskEntry> list)
    {
        foreach (dynamic task in folder.GetTasks(1)) // 1 = include hidden tasks
        {
            try
            {
                var programs = new List<string>();
                foreach (dynamic action in task.Definition.Actions)
                    if ((int)action.Type == 0) programs.Add((string)action.Path); // 0 = start a program
                list.Add(new TaskEntry((string)task.Path, (string?)task.Definition.RegistrationInfo.Description, programs, (bool)task.Enabled));
            }
            catch (System.Runtime.InteropServices.COMException) { } // a task we may not read
        }
        foreach (dynamic sub in folder.GetFolders(0)) Walk(sub, list);
    }

    public void SetTaskEnabled(string path, bool enabled)
    {
        dynamic scheduler = Scheduler() ?? throw new InvalidOperationException("Task Scheduler is not available.");
        int cut = path.LastIndexOf('\\');
        dynamic folder = scheduler.GetFolder(cut <= 0 ? @"\" : path[..cut]);
        dynamic task = folder.GetTask(path[(cut + 1)..]);
        task.Enabled = enabled;
    }

    private static dynamic? Scheduler()
    {
        if (Type.GetTypeFromProgID("Schedule.Service") is not { } type) return null;
        dynamic scheduler = Activator.CreateInstance(type)!;
        scheduler.Connect();
        return scheduler;
    }

    public IReadOnlyList<string> RunningProcesses()
    {
        var names = new List<string>();
        foreach (var p in Process.GetProcesses())
            using (p)
                names.Add(p.ProcessName.ToLowerInvariant() + ".exe");
        return names;
    }

    public void Kill(string processName)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName)))
            using (p)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
    }
}
