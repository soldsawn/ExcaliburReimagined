namespace Excalibur.Core.Settings;

/// <summary>
/// Loads and saves <see cref="ServiceSettings"/>. Thread-safe; saving is atomic and best effort:
/// if the file cannot be written the change still applies for this run (a settings problem
/// must never take the fan loop down, docs/07).
/// </summary>
public sealed class ServiceSettingsStore(string path)
{
    private readonly object _gate = new();
    private ServiceSettings? _current;

    public string Path { get; } = path;

    /// <summary>Raised (outside the lock) after a change was applied, saved or not.</summary>
    public event Action<ServiceSettings>? Changed;

    /// <summary>Problems found when the file was last read (migrations, invalid values).</summary>
    public IReadOnlyList<string> LoadProblems { get; private set; } = [];

    public ServiceSettings Current
    {
        get { lock (_gate) return _current ??= Load(); }
    }

    /// <summary>Re-reads the file.</summary>
    public ServiceSettings Reload()
    {
        lock (_gate) return _current = Load();
    }

    /// <summary>
    /// Validates and stores a change. Returns the errors (nothing changes then), or an empty list.
    /// <paramref name="saved"/> is false when it applies but could not be written to disk.
    /// </summary>
    public IReadOnlyList<string> Update(Func<ServiceSettings, ServiceSettings> change, out bool saved)
    {
        ServiceSettings next;
        lock (_gate)
        {
            next = change(_current ??= Load()) with { Version = ServiceSettings.CurrentVersion };
            var errors = next.Validate();
            saved = false;
            if (errors.Count > 0) return errors;
            _current = next;
            try
            {
                AtomicFile.WriteAllText(Path, next.ToJson());
                saved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke(next);
        return [];
    }

    private ServiceSettings Load()
    {
        string json;
        try
        {
            json = File.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            LoadProblems = [];
            return new ServiceSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadProblems = [$"could not read {Path} ({ex.Message}); using defaults"];
            return new ServiceSettings();
        }
        var (settings, problems) = ServiceSettings.Parse(json);
        LoadProblems = problems;
        return settings;
    }
}
