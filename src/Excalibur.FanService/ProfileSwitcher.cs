using Excalibur.Core.Fan;
using Excalibur.Core.Settings;

namespace Excalibur.FanService;

/// <summary>
/// Switches the fan profile the way a user command does (loop + saved choice), for the pipe and
/// for the power automation alike.
/// </summary>
public sealed class ProfileSwitcher(FanControlLoop loop, ServiceSettingsStore settings, ILogger<ProfileSwitcher> logger)
{
    public void Switch(FanProfile profile, DateTimeOffset now, string? source)
    {
        loop.SetProfile(profile, now, source);
        // Best effort: the profile still applies for this run if it cannot be saved (docs/07).
        settings.Update(s => s with { Profile = profile.Name }, out bool saved);
        if (!saved) logger.LogWarning("profile applied but could not be saved to {Path}", settings.Path);
    }

    public void Switch(string profileName, string source)
    {
        var profile = settings.Current.FindProfile(profileName) ?? throw new ArgumentException($"unknown profile '{profileName}'");
        Switch(profile, DateTimeOffset.Now, source);
    }
}
