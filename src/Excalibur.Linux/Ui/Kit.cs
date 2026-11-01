using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Excalibur.Linux.Ui.Controls;

namespace Excalibur.Linux.Ui;

/// <summary>
/// The building blocks the Windows pages make with InfoPage.Text / Card / Page / Tip, so the ported pages read the same:
/// a style name is a class here (Theme.axaml), a resource key is followed live like WPF's SetResourceReference.
/// </summary>
internal static class Kit
{
    /// <summary>Follows a palette resource, as WPF's SetResourceReference (it changes with the theme).</summary>
    public static T Res<T>(this T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }

    public static T Classed<T>(this T control, params string[] classes) where T : StyledElement
    {
        control.Classes.AddRange(classes);
        return control;
    }

    public static TextBlock Text(string text, string style, double top = 0, double bottom = 0, bool accent = false, double width = double.NaN)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, bottom), Width = width };
        t.Classes.Add(style);
        if (accent) t.Res(TextBlock.ForegroundProperty, "Accent");
        return t;
    }

    public static Control Page(string title, string subtitle, params Control[] cards)
    {
        var panel = new StackPanel();
        panel.Children.Add(Text(title, "PageTitle"));
        panel.Children.Add(Text(subtitle, "Caption", top: 2, bottom: 18));
        foreach (var card in cards) panel.Children.Add(card);
        return panel;
    }

    public static BracketCard Card(string title, params Control[] content)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(title, "CardTitle", bottom: 8));
        foreach (var c in content) stack.Children.Add(c);
        return new BracketCard { Child = stack, Margin = new Thickness(0, 0, 0, 14) }.Classed("CardBox");
    }

    /// <summary>A small (i) that explains a term in a tooltip when hovered.</summary>
    public static TextBlock Tip(string explanation)
    {
        var t = new TextBlock
        {
            Text = "", FontSize = 14, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Help),
        };
        t.Classes.Add("Icon");
        t.Res(TextBlock.ForegroundProperty, "SubText");
        ToolTip.SetTip(t, new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
        ToolTip.SetShowDelay(t, 150);
        return t;
    }

    /// <summary>A text followed by its (i) explanation.</summary>
    public static StackPanel Labeled(string text, string style, string explanation, double top = 0, double bottom = 0)
    {
        var label = Text(text, style);
        label.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, top, 0, bottom), Children = { label, Tip(explanation) } };
    }

    public static Button Button(string content, string style = "PlainButton") => new Button { Content = content }.Classed(style);

    public static RadioButton Segment(object content, string group) => new RadioButton { Content = content, GroupName = group }.Classed("Segment");
}
