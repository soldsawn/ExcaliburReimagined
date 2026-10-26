using System.Runtime.InteropServices;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.App.Tray;

/// <summary>
/// Makes the Windows power plan and Windows' own power mode (Settings > Power: best power efficiency / balanced / best
/// performance) follow the laptop's performance mode, as the user set them in <see cref="UserSettings.WindowsPower"/>; the
/// stock software switches plans the same way. Off by default. Windows keeps one power mode for the charger and one for
/// the battery, so the rule is applied again when the power source changes; it ignores the power mode unless the Balanced
/// plan is active, so the plan goes first.
/// </summary>
internal sealed class WindowsPowerMode(UserSettingsStore settings)
{
    private (PowerMode Mode, bool OnBattery, WindowsPowerRule Rule)? _applied;

    /// <summary>Call with every status; writes only when the option is on and the mode, the source or the rule changed.</summary>
    public void Follow(PowerStatusDto? power)
    {
        var rules = settings.Current.WindowsPower;
        if (!rules.Enabled) { _applied = null; return; }
        if (power?.Mode is not { } mode) return;
        var rule = rules.RuleFor(mode, power.OnBattery);
        var key = (mode, power.OnBattery, rule);
        if (_applied == key) return;
        // A refusal is not retried every poll; the next change tries again.
        _applied = key;
        if (rule.Plan is { } plan)
        {
            uint r = PowerSetActiveScheme(IntPtr.Zero, plan);
            TrayLog.Write($"windows power plan: {mode}{(power.OnBattery ? " on battery" : "")} -> {plan} ({Result(r)})");
        }
        if (rule.Mode is { } overlay)
        {
            uint r = PowerSetActiveOverlayScheme(OverlayGuid(overlay));
            TrayLog.Write($"windows power mode: {mode}{(power.OnBattery ? " on battery" : "")} -> {overlay} ({Result(r)})");
        }
    }

    private static string Result(uint r) => r == 0 ? "ok" : $"error {r}";

    private static Guid OverlayGuid(WindowsPowerOverlay overlay) => overlay switch
    {
        WindowsPowerOverlay.BestEfficiency => new("961cc777-2547-4f9d-8174-7d86181b8a7a"),
        WindowsPowerOverlay.BestPerformance => new("ded574b5-45a0-4f42-8737-46345c09c238"),
        _ => Guid.Empty,
    };

    /// <summary>The power plans on this computer, with their names, for the settings page.</summary>
    public static IReadOnlyList<(Guid Id, string Name)> Plans()
    {
        var plans = new List<(Guid, string)>();
        int size = Marshal.SizeOf<Guid>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (uint i = 0; ; i++)
            {
                uint bytes = (uint)size;
                if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buffer, ref bytes) != 0) break;
                var id = Marshal.PtrToStructure<Guid>(buffer);
                plans.Add((id, FriendlyName(id) ?? id.ToString()));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return plans;
    }

    private static string? FriendlyName(Guid plan)
    {
        uint bytes = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref bytes) != 0 || bytes == 0) return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            return PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, buffer, ref bytes) == 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const uint AccessScheme = 16; // ACCESS_SCHEME

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, in Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupGuid, uint accessFlags,
        uint index, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid,
        IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);
}
