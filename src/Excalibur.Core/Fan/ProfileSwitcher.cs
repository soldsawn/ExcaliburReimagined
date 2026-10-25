using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Settings;

namespace Excalibur.Core.Fan;

/// <summary>
/// Switches the fan profile the way a user command does (loop + saved choice), for the pipe and
/// for the power automation alike. A running program's rule (<see cref="SetOverride"/>) sits above the saved choice:
/// the power automation then only changes the saved choice, which comes back when the program closes; the user switching
/// the profile ends the program's part (it is the user's choice now).
/// </summary>
public sealed class ProfileSwitcher(FanControlLoop loop, ServiceSettingsStore settings, Action<string>? log = null)
{
    private readonly object _gate = new();
    private string? _override;

    /// <summary>The profile a running program's rule put in force, or null.</summary>
    public string? Override
    {
        get { lock (_gate) return _override; }
    }

    public void Switch(FanProfile profile, DateTimeOffset now, string? source)
    {
        lock (_gate)
        {
            if (_override is null || source != FanSource.Automation)
            {
                _override = null;
                loop.SetProfile(profile, now, source);
            }
            // Best effort: the profile still applies for this run if it cannot be saved (docs/07).
            settings.Update(s => s with { Profile = profile.Name }, out bool saved);
            if (!saved) log?.Invoke($"profile applied but could not be saved to {settings.Path}");
        }
    }

    public void Switch(string profileName, string source)
    {
        var profile = settings.Current.FindProfile(profileName) ?? throw new ArgumentException($"unknown profile '{profileName}'");
        Switch(profile, DateTimeOffset.Now, source);
    }

    /// <summary>Puts a program rule's profile in force without saving it, or with null goes back to the saved choice.</summary>
    public void SetOverride(string? profileName, DateTimeOffset now)
    {
        lock (_gate)
        {
            var profile = profileName is null ? null : settings.Current.FindProfile(profileName);
            if (profileName is not null && profile is null) log?.Invoke($"app rule: unknown fan profile '{profileName}' ignored");
            if (profile is not null)
            {
                _override = profile.Name;
                loop.SetProfile(profile, now, FanSource.App);
            }
            else if (_override is not null)
            {
                _override = null;
                loop.SetProfile(settings.Current.FindProfile(settings.Current.Profile) ?? FanProfile.Balanced, now, FanSource.App);
            }
        }
    }
}
