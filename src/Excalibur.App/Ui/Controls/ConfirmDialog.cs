using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Excalibur.App.Ui.Pages;

namespace Excalibur.App.Ui.Controls;

/// <summary>
/// A yes/no question in the app's own theme, in place of Windows' MessageBox (which ignores it): a small window centred on
/// the owner, dragged by any spot, Esc = cancel. Focus starts on cancel so Enter never deletes by accident.
/// </summary>
internal sealed class ConfirmDialog : Window
{
    /// <summary>Use <see cref="Ask"/>; built directly only for the development snapshot.</summary>
    internal ConfirmDialog(string title, string message, string confirm, bool danger)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 14;

        var body = InfoPage.Text(message, "Body", top: 10, bottom: 18);
        body.TextWrapping = TextWrapping.Wrap;
        body.MaxWidth = 440;

        var cancel = new Button { Content = "Vazgeç", IsCancel = true, MinWidth = 96 };
        cancel.SetResourceReference(StyleProperty, "PlainButton");
        var ok = new Button { Content = confirm, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        ok.SetResourceReference(StyleProperty, danger ? "DangerButton" : "AccentButton");
        ok.Click += (_, _) => DialogResult = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { MinWidth = 320 };
        panel.Children.Add(InfoPage.Text(title, "CardTitle"));
        panel.Children.Add(body);
        panel.Children.Add(buttons);

        var card = new Border { Child = panel };
        card.SetResourceReference(StyleProperty, "CardBox");
        Content = card;

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Loaded += (_, _) => cancel.Focus();
    }

    /// <summary>True when the user confirmed. <paramref name="danger"/> paints the confirm button red (deleting).</summary>
    public static bool Ask(DependencyObject owner, string title, string message, string confirm, bool danger = false)
    {
        var dialog = new ConfirmDialog(title, message, confirm, danger) { Owner = GetWindow(owner) };
        return dialog.ShowDialog() == true;
    }
}
