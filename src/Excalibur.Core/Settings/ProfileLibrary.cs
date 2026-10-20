using Excalibur.Core.Fan;
using Excalibur.Core.Power;

namespace Excalibur.Core.Settings;

/// <summary>
/// The changes the pipe commands make to the stored profiles (docs/09 A4), as pure functions over
/// <see cref="ServiceSettings"/>: the store validates the result before anything is kept, so these
/// only have to produce the intended settings, including the clean-up a deletion needs.
/// </summary>
public static class ProfileLibrary
{
    /// <summary>Adds the profile, or replaces the custom profile of the same name (case-insensitive).</summary>
    public static ServiceSettings Save(ServiceSettings s, CustomProfile profile)
    {
        var clean = profile.Normalized();
        var list = s.CustomProfiles.Where(c => !string.Equals(c.Name, clean.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        int at = s.CustomProfiles.ToList().FindIndex(c => string.Equals(c.Name, clean.Name, StringComparison.OrdinalIgnoreCase));
        list.Insert(at < 0 ? list.Count : at, clean);
        // An edit may change the capitalisation of a name that is already chosen somewhere.
        string Fix(string n) => string.Equals(n, clean.Name, StringComparison.OrdinalIgnoreCase) ? clean.Name : n;
        return s with
        {
            CustomProfiles = list,
            Profile = Fix(s.Profile),
            Slots = s.Slots?.Select(Fix).ToList(),
            PowerAutomation = s.PowerAutomation with
            {
                OnAc = FixRule(s.PowerAutomation.OnAc),
                OnBattery = FixRule(s.PowerAutomation.OnBattery),
            },
        };

        PowerRule FixRule(PowerRule r) => r.FanProfile is { } f ? r with { FanProfile = Fix(f) } : r;
    }

    /// <summary>
    /// Removes a custom profile and everything that pointed at it: the active profile falls back to
    /// Balanced, automation rules forget it, and the slots drop it (an empty list means the default four).
    /// </summary>
    public static ServiceSettings Delete(ServiceSettings s, string name)
    {
        bool Is(string? n) => string.Equals(n, name, StringComparison.OrdinalIgnoreCase);
        PowerRule Clear(PowerRule r) => Is(r.FanProfile) ? r with { FanProfile = null } : r;
        var slots = s.Slots?.Where(n => !Is(n)).ToList();
        return s with
        {
            CustomProfiles = s.CustomProfiles.Where(c => !Is(c.Name)).ToList(),
            Profile = Is(s.Profile) ? FanProfile.Balanced.Name : s.Profile,
            Slots = slots is { Count: > 0 } ? slots : null,
            PowerAutomation = s.PowerAutomation with { OnAc = Clear(s.PowerAutomation.OnAc), OnBattery = Clear(s.PowerAutomation.OnBattery) },
        };
    }

    public static ServiceSettings SetSlots(ServiceSettings s, IReadOnlyList<string> names) => s with
    {
        Slots = names.Select(n => s.FindProfile(n)?.Name ?? n).ToList(),
    };
}
