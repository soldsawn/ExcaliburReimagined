using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Excalibur.App.Ui.Controls;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Lighting;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Aydınlatma: the keyboard light (docs/09 B5), all on one screen. On top the backlit keyboard (a click picks a zone);
/// below, the light settings on the left and the colour wheel on the right. Every change is sent at once (setLighting);
/// the service keeps it and shows it again after a restart or sleep. Brightness has the light key's three levels,
/// "Kapalı" included, and follows the key (status LightBrightness), so the key and this page always agree.
/// </summary>
internal sealed class LightingPage : UserControl
{
    private static readonly (LightEffect Effect, string Label)[] Effects =
        [(LightEffect.Static, "Sabit"), (LightEffect.Breathing, "Nefes"), (LightEffect.ColorCycle, "Renk döngüsü")];
    private static readonly string[] Levels = ["Kapalı", "Düşük", "Yüksek"];
    private static readonly (string Name, string Hex)[] Swatches =
    [
        ("Kırmızı", "#FF0000"), ("Turuncu", "#FF6A00"), ("Sarı", "#FFD000"), ("Yeşil", "#00FF40"), ("Camgöbeği", "#00FFFF"),
        ("Mavi", "#0050FF"), ("Mor", "#8A00FF"), ("Pembe", "#FF0080"), ("Beyaz", "#FFFFFF"),
    ];
    private static readonly (string Label, int Minutes)[] IdleChoices =
    [
        ("Hiçbir zaman", 0), ("1 dakika", 1), ("2 dakika", 2), ("5 dakika", 5), ("10 dakika", 10), ("15 dakika", 15), ("30 dakika", 30),
    ];

    private readonly FanConnection _connection;
    private readonly KeyboardView _keyboard = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly ColourWheel _wheel = new() { Margin = new Thickness(0, 0, 18, 0) };
    private readonly Dictionary<LightEffect, RadioButton> _effects = [];
    private readonly RadioButton[] _levels = new RadioButton[3];
    private readonly RadioButton[] _zones = new RadioButton[4];
    private readonly StackPanel _zoneRow;
    private readonly FrameworkElement _colourPanel;
    private readonly TextBox _hex = new() { Width = 96, Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0) };
    private readonly ComboBox _idle = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _effectNote, _colourNote, _error;
    private KeyboardLighting _light = new();
    private int _zone = -1;
    private bool _rendering, _sending;

    public LightingPage(FanConnection connection)
    {
        _connection = connection;
        _keyboard.ZoneClicked += z => Pick(_zone == z ? -1 : z);

        // ---- zones, under the keyboard ----
        _zoneRow = new StackPanel { Orientation = Orientation.Horizontal };
        string[] zoneNames = ["Tüm klavye", "Sol", "Orta", "Sağ"];
        for (int i = 0; i < 4; i++)
        {
            int zone = i - 1;
            _zones[i] = Segment("zone" + GetHashCode(), zoneNames[i]);
            _zones[i].Checked += (_, _) => { if (!_rendering) Pick(zone); };
            _zoneRow.Children.Add(_zones[i]);
        }

        // ---- left: effect, brightness, idle ----
        var effects = new WrapPanel();
        foreach (var (effect, label) in Effects)
        {
            var b = Segment("effect" + GetHashCode(), label);
            b.Checked += (_, _) => Change(_light with { Effect = effect });
            _effects[effect] = b;
            effects.Children.Add(b);
        }
        _effectNote = InfoPage.Text("", "Caption", top: 2, bottom: 12);
        var levels = new WrapPanel();
        for (int i = 0; i < Levels.Length; i++)
        {
            int level = i;
            _levels[i] = Segment("level" + GetHashCode(), Levels[i]);
            // A click on the level already shown still sends: the light key may have changed it a moment ago.
            _levels[i].Click += (_, _) => Change(_light with { Brightness = level }, force: true);
            levels.Children.Add(_levels[i]);
        }
        _idle.SetResourceReference(StyleProperty, "ThemedCombo");
        _idle.ItemsSource = IdleChoices.Select(c => c.Label).ToList();
        _idle.SelectionChanged += (_, _) =>
        {
            if (_idle.SelectedIndex >= 0) Change(_light with { IdleOffMinutes = IdleChoices[_idle.SelectedIndex].Minutes });
        };
        var settingsCard = InfoPage.Card("Işık",
            Label("Efekt"), effects, _effectNote,
            Label("Parlaklık", "Klavyedeki ışık tuşu da bu üç kademe arasında geçer; seçtiğiniz renkler korunur."), levels,
            Label("Boşta söndür", "Klavyeye ve fareye bu süre dokunulmazsa ışık söner, dokununca hemen geri yanar."),
            _idle);

        // ---- right: colour ----
        _wheel.Picking += c => Preview(c);
        _wheel.Picked += c => SetColour(Hex(c));
        var swatches = new WrapPanel { Width = 5 * 34 };
        foreach (var (name, hex) in Swatches) swatches.Children.Add(Swatch(name, hex));
        _hex.SetResourceReference(Control.ForegroundProperty, "Text");
        _hex.SetResourceReference(Control.BackgroundProperty, "Bg");
        _hex.SetResourceReference(Control.BorderBrushProperty, "Border");
        _hex.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };
        _hex.LostFocus += (_, _) => ApplyHex();
        _colourNote = InfoPage.Text("", "Caption", top: 10);
        _colourNote.TextWrapping = TextWrapping.Wrap;
        _colourNote.MaxWidth = 5 * 34;
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { _hex } };
        var colourSide = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { swatches, hexRow, _colourNote } };
        var colourBody = new StackPanel { Orientation = Orientation.Horizontal, Children = { _wheel, colourSide } };
        _colourPanel = colourBody;
        var colourCard = InfoPage.Card("Renk", colourBody);

        var columns = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(14) }, new ColumnDefinition { Width = GridLength.Auto } },
        };
        Grid.SetColumn(colourCard, 2);
        columns.Children.Add(settingsCard);
        columns.Children.Add(colourCard);

        _error = InfoPage.Text("", "Caption", top: 2);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        _error.Visibility = Visibility.Collapsed;

        Content = new StackPanel
        {
            Children =
            {
                InfoPage.Text("Aydınlatma", "PageTitle"),
                InfoPage.Text("Klavyenin üç bölgeli ışığı. Değişiklikler hemen uygulanır ve bilgisayar yeniden açılınca da korunur.", "Caption", top: 2, bottom: 18),
                InfoPage.Card("Klavye", _keyboard, _zoneRow),
                columns,
                _error,
            },
        };

        // The light key changes the level in the service; follow it so the page never shows a stale level.
        _connection.Changed += () =>
        {
            if (_connection.Status?.LightBrightness is { } level && level != _light.Brightness && !_sending)
            {
                _light = _light with { Brightness = level };
                Render();
            }
        };
        Loaded += async (_, _) =>
        {
            await _connection.RefreshSettingsAsync();
            if (_connection.Settings?.Lighting is { } l) _light = l;
            Render();
        };
        Render();
    }

    // ---- building blocks -------------------------------------------------------------------

    private static UIElement Label(string text, string? tip = null) =>
        tip is null ? InfoPage.Text(text, "Kicker", bottom: 6) : InfoPage.Labeled(text, "Kicker", tip, top: 4, bottom: 6);

    private RadioButton Segment(string group, string text)
    {
        var b = new RadioButton { GroupName = group, Content = text, Margin = new Thickness(0, 0, 6, 6) };
        b.SetResourceReference(StyleProperty, "Segment");
        return b;
    }

    private Button Swatch(string name, string hex)
    {
        var button = new Button
        {
            Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, ToolTip = name,
            Template = SwatchTemplate(), Background = new SolidColorBrush(ToColour(hex)),
        };
        button.SetResourceReference(BorderBrushProperty, "Border");
        button.Click += (_, _) => SetColour(hex);
        return button;
    }

    private static ControlTemplate SwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private static Color ToColour(string hex) =>
        KeyboardLighting.ParseColour(hex) is var (r, g, b) ? Color.FromRgb((byte)r, (byte)g, (byte)b) : Colors.Black;

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ---- actions ---------------------------------------------------------------------------

    private void Pick(int zone)
    {
        _zone = _light.Effect == LightEffect.Static ? zone : -1;
        Render();
    }

    private void ApplyHex()
    {
        string text = _hex.Text.Trim().ToUpperInvariant();
        if (!text.StartsWith('#')) text = "#" + text;
        if (KeyboardLighting.ParseColour(text) is null) { Render(); return; }
        SetColour(text);
    }

    /// <summary>While the wheel is dragged: the keyboard drawing shows the colour; nothing is sent until release.</summary>
    private void Preview(Color c)
    {
        _keyboard.Colours = Shown(Paint(_light, Hex(c)));
        _hex.Text = Hex(c);
    }

    /// <summary>The colour goes to the picked zone, or to all three (always with Breathing: it has one colour).</summary>
    private KeyboardLighting Paint(KeyboardLighting l, string hex) => _zone switch
    {
        0 => l with { Left = hex },
        1 => l with { Middle = hex },
        2 => l with { Right = hex },
        _ => l with { Left = hex, Middle = hex, Right = hex },
    };

    private void SetColour(string hex) => Change(Paint(_light, hex));

    private async void Change(KeyboardLighting next, bool force = false)
    {
        if (_rendering || (next == _light && !force)) return;
        var previous = _light;
        _light = next;
        if (next.Effect != LightEffect.Static) _zone = -1;
        Render();
        _sending = true;
        var r = await _connection.RequestAsync(new FanRequest("setLighting", Lighting: next));
        _sending = false;
        string? error = r is null ? "Fan servisine ulaşılamadı; ışık değiştirilemedi." : r.Ok ? null : "Işık ayarı kabul edilmedi.";
        if (error is not null && _light == next) { _light = previous; Render(); }
        _error.Text = error ?? "";
        _error.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- render ----------------------------------------------------------------------------

    private static Color[] Shown(KeyboardLighting l) => l.Effect == LightEffect.Static
        ? [ToColour(l.Left), ToColour(l.Middle), ToColour(l.Right)]
        : [ToColour(l.Left), ToColour(l.Left), ToColour(l.Left)];

    private void Render()
    {
        _rendering = true;
        bool zoned = _light.Effect == LightEffect.Static;
        _keyboard.Colours = Shown(_light);
        _keyboard.Effect = _light.Effect;
        _keyboard.Brightness = _light.Brightness;
        _keyboard.Selected = _zone;
        _keyboard.IsEnabled = zoned;
        _zoneRow.Visibility = zoned ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < 4; i++) _zones[i].IsChecked = _zone == i - 1;

        _colourPanel.IsEnabled = _light.Effect != LightEffect.ColorCycle;
        _colourPanel.Opacity = _colourPanel.IsEnabled ? 1 : 0.4;
        string shown = _zone switch { 0 => _light.Left, 1 => _light.Middle, 2 => _light.Right, _ => _light.Left };
        _wheel.Colour = ToColour(shown);
        if (!_hex.IsKeyboardFocused) _hex.Text = shown;
        _colourNote.Text = _light.Effect switch
        {
            LightEffect.Static => _zone switch
            {
                < 0 => "Renk tüm klavyeye uygulanır. Tek bölge için klavyede ona tıklayın.",
                0 => "Renk yalnız sol bölgeye uygulanır.",
                1 => "Renk yalnız orta bölgeye uygulanır.",
                _ => "Renk yalnız sağ bölgeye uygulanır.",
            },
            LightEffect.Breathing => "Nefes tüm klavyede tek renkle çalışır.",
            _ => "Renk döngüsü renkleri kendisi seçer.",
        };
        _effectNote.Text = _light.Effect switch
        {
            LightEffect.Static => "Her bölge kendi renginde sürekli yanar.",
            LightEffect.Breathing => "Işık tek renkte yavaşça yanıp söner.",
            _ => "Renkler sırayla değişir.",
        };
        foreach (var (effect, button) in _effects) button.IsChecked = _light.Effect == effect;
        for (int i = 0; i < 3; i++) _levels[i].IsChecked = _light.Brightness == i;
        _idle.SelectedIndex = Array.FindIndex(IdleChoices, c => c.Minutes == _light.IdleOffMinutes);
        _rendering = false;
    }
}
