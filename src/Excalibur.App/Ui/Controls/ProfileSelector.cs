using System.Windows;
using System.Windows.Controls;
using Excalibur.App.Tray;
using Excalibur.Core.Fan.Ipc;

namespace Excalibur.App.Ui.Controls;

/// <summary>
/// The quick-access fan profiles (the user's slots; by default the four built-ins) as segment buttons. A click asks the service; the selection
/// then follows the service's status, so it also reflects changes made from the tray.
/// </summary>
internal sealed class ProfileSelector : WrapPanel
{
    private readonly FanConnection _connection;
    private readonly Dictionary<string, RadioButton> _buttons = [];
    private bool _rendering;

    public event Action<string?>? CommandFailed;

    public ProfileSelector(FanConnection connection)
    {
        _connection = connection;
        _group = "profile" + GetHashCode();
        Build();
        _connection.Catalog.Changed += () => { Build(); Render(); };
        _connection.Changed += Render;
        Render();
    }

    private readonly string _group;

    private void Build()
    {
        _buttons.Clear();
        Children.Clear();
        foreach (var (name, label) in _connection.Catalog.SlotButtons())
        {
            var button = new RadioButton { Content = label, GroupName = _group, Margin = new Thickness(0, 0, 8, 8), ToolTip = ProfileNames.Label(name) };
            button.SetResourceReference(StyleProperty, "Segment");
            button.MinWidth = 118;
            button.Checked += async (_, _) =>
            {
                if (_rendering) return;
                string? error = await _connection.SendAsync(new FanRequest("setProfile", Profile: name));
                if (error is not null) CommandFailed?.Invoke(error);
            };
            _buttons[name] = button;
            Children.Add(button);
        }
    }

    private void Render()
    {
        _rendering = true;
        var s = _connection.Status;
        foreach (var (name, button) in _buttons)
        {
            button.IsEnabled = _connection.Connected;
            button.IsChecked = _connection.Connected && string.Equals(s?.Profile, name, StringComparison.OrdinalIgnoreCase);
        }
        _rendering = false;
    }
}
