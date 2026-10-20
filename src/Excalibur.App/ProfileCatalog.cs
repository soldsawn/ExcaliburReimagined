using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Settings;

namespace Excalibur.App;

/// <summary>
/// The profiles the service knows (built-in and the user's own) and which of them the quick panel and
/// the tray menu offer (docs/09 A4). Both processes keep one, refreshed from <c>listProfiles</c>; until
/// the first answer it holds the four built-ins in their default order. Names of custom profiles are
/// what the user typed, so they are shown as they are.
/// </summary>
internal sealed class ProfileCatalog
{
    private const int ShortLength = 11;

    public IReadOnlyList<ProfileInfoDto> Profiles { get; private set; } =
        FanProfile.BuiltIn.Select(p => p.IsEcAuto
            ? new ProfileInfoDto(p.Name, true, null, null)
            : new ProfileInfoDto(p.Name, true, p.CpuCurve.Points, p.GpuCurve.Points)).ToList();

    public IReadOnlyList<string> Slots { get; private set; } = ServiceSettings.DefaultSlots;

    /// <summary>Raised when the profile list or the slots really changed.</summary>
    public event Action? Changed;

    public void Update(FanResponse response)
    {
        if (response.Profiles is null) return;
        var slots = response.Slots is { Count: > 0 } s ? s : ServiceSettings.DefaultSlots;
        bool same = Profiles.Count == response.Profiles.Count
            && Profiles.Zip(response.Profiles).All(p => Equal(p.First, p.Second))
            && Slots.SequenceEqual(slots);
        Profiles = response.Profiles;
        Slots = slots;
        if (!same) Changed?.Invoke();
    }

    private static bool Equal(ProfileInfoDto a, ProfileInfoDto b) =>
        a.Name == b.Name && a.BuiltIn == b.BuiltIn
        && (a.Cpu is null ? b.Cpu is null : b.Cpu is not null && a.Cpu.SequenceEqual(b.Cpu))
        && (a.Gpu is null ? b.Gpu is null : b.Gpu is not null && a.Gpu.SequenceEqual(b.Gpu));

    public ProfileInfoDto? Get(string name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The curves of a profile, ready to draw; null for EcAuto and for names the service did not list.</summary>
    public FanProfile? Find(string name)
    {
        if (Get(name) is not { Cpu: { } cpu } info) return null;
        try { return new FanProfile(info.Name, new FanCurve(cpu), new FanCurve(info.Gpu ?? cpu), IsEcAuto: false); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>The slots as (name, short label) in order; names the service no longer lists are skipped.</summary>
    public IReadOnlyList<(string Name, string Short)> SlotButtons() =>
        Slots.Where(n => Get(n) is not null).Select(n => (Get(n)!.Name, Short(n))).ToList();

    /// <summary>What a button or a chip says: the built-in short label, or the custom name cut to fit.</summary>
    public static string Short(string name)
    {
        foreach (var (n, _, s) in Tray.ProfileNames.All)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return s;
        return name.Length <= ShortLength ? name : name[..(ShortLength - 1)] + "…";
    }
}
