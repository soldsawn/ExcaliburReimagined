using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Excalibur.Linux.Ui.Controls;

/// <summary>
/// A yes/no question in the app's own theme, as on Windows: a small borderless window centred on the owner, dragged by any
/// spot, Esc = cancel. Focus starts on cancel so Enter never deletes by accident.
/// </summary>
internal sealed class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirm, bool danger)
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var body = Kit.Text(message, "Body", top: 10, bottom: 18);
        body.TextWrapping = TextWrapping.Wrap;
        body.MaxWidth = 440;

        var cancel = Kit.Button("Vazgeç");
        cancel.MinWidth = 96;
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close(false);
        var ok = Kit.Button(confirm, danger ? "DangerButton" : "AccentButton");
        ok.MinWidth = 96;
        ok.Margin = new Thickness(8, 0, 0, 0);
        ok.Click += (_, _) => Close(true);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } };
        var panel = new StackPanel { MinWidth = 320, Children = { Kit.Text(title, "CardTitle"), body, buttons } };
        Content = new BracketCard { Child = panel }.Classed("CardBox");

        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        Opened += (_, _) => cancel.Focus();
    }

    /// <summary>True when the user confirmed. <paramref name="danger"/> paints the confirm button red (deleting).</summary>
    public static async Task<bool> Ask(Control owner, string title, string message, string confirm, bool danger = false)
    {
        if (TopLevel.GetTopLevel(owner) is not Window window) return false;
        return await new ConfirmDialog(title, message, confirm, danger).ShowDialog<bool>(window);
    }
}
