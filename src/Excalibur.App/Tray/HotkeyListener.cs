using System.Runtime.InteropServices;
using System.Windows.Forms;
using Excalibur.Core.Settings;

namespace Excalibur.App.Tray;

/// <summary>What a global shortcut does.</summary>
internal enum HotkeyAction { Profile = 1, PowerMode = 2, Window = 3 }

/// <summary>
/// Registers the user's global shortcuts with Windows (RegisterHotKey on a message-only window) and raises
/// <see cref="Pressed"/> on the UI thread. <see cref="Apply"/> re-registers them when the settings change and returns the
/// combinations another program already holds.
/// </summary>
internal sealed class HotkeyListener : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint NoRepeat = 0x4000;
    private static readonly IntPtr MessageOnly = new(-3);
    private readonly List<int> _registered = [];
    private HotkeySettings? _applied;

    public event Action<HotkeyAction>? Pressed;

    public HotkeyListener() => CreateHandle(new CreateParams { Parent = MessageOnly });

    /// <summary>Registers the shortcuts (nothing when they are off); null when unchanged since last time.</summary>
    public IReadOnlyList<string>? Apply(HotkeySettings settings)
    {
        if (settings == _applied) return null;
        _applied = settings;
        Unregister();
        var taken = new List<string>();
        if (!settings.Enabled) return taken;
        foreach (var (action, text) in new[] { (HotkeyAction.Profile, settings.Profile), (HotkeyAction.PowerMode, settings.PowerMode), (HotkeyAction.Window, settings.Window) })
        {
            if (Hotkey.Parse(text) is not { } key) continue;
            if (RegisterHotKey(Handle, (int)action, key.Modifiers | NoRepeat, key.Key)) _registered.Add((int)action);
            else taken.Add(key.ToString());
        }
        return taken;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && Enum.IsDefined(typeof(HotkeyAction), (int)m.WParam)) Pressed?.Invoke((HotkeyAction)(int)m.WParam);
        base.WndProc(ref m);
    }

    private void Unregister()
    {
        foreach (int id in _registered) UnregisterHotKey(Handle, id);
        _registered.Clear();
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
