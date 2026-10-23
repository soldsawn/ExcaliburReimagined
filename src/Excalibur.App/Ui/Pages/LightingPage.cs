using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Excalibur.App.Ui.Controls;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Lighting;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Aydınlatma: the keyboard light (docs/09 B5). The drawing shows the three zones in their colours; a click picks one.
/// Every change is sent at once (setLighting); the service keeps it and shows it again after a restart or sleep.
/// Brightness has the light key's three levels, "Kapalı" included, so the key and this page always agree.
/// </summary>
internal sealed class LightingPage : UserControl
{
    private static readonly (LightEffect Effect, string Label, string Detail)[] Effects =
    [
        (LightEffect.Static, "Sabit", "bölge bölge renk"),
        (LightEffect.Breathing, "Nefes", "tek renk, yavaşça yanıp söner"),
        (LightEffect.ColorCycle, "Renk döngüsü", "renkler sırayla değişir"),
    ];
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
    private readonly KeyboardView _keyboard = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly Dictionary<LightEffect, RadioButton> _effects = [];
    private readonly RadioButton[] _levels = new RadioButton[3];
    private readonly RadioButton[] _zones = new RadioButton[4];
    private readonly WrapPanel _zoneRow, _swatchRow;
    private readonly TextBox _hex = new() { Width = 90, Margin = new Thickness(4, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _idle = new() { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _colourNote, _error;
    private KeyboardLighting _light = new();
    private int _zone = -1;
    private bool _rendering;

    public LightingPage(FanConnection connection)
    {
        _connection = connection;
        _keyboard.ZoneClicked += z => Pick(_zone == z ? -1 : z);

        var effects = new WrapPanel();
        string group = "effect" + GetHashCode();
        foreach (var (effect, label, detail) in Effects)
        {
            var b = Segment(group, new StackPanel
            {
                Children = { new TextBlock { Text = label, FontWeight = FontWeights.SemiBold }, InfoPage.Text(detail, "Caption", top: 2) },
            }, minWidth: 190, height: 58);
            b.Checked += (_, _) => Change(_light with { Effect = effect });
            _effects[effect] = b;
            effects.Children.Add(b);
        }

        var levels = new WrapPanel();
        for (int i = 0; i < Levels.Length; i++)
        {
            int level = i;
            _levels[i] = Segment("level" + GetHashCode(), Levels[i]);
            _levels[i].Checked += (_, _) => Change(_light with { Brightness = level });
            levels.Children.Add(_levels[i]);
        }

        _zoneRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        string[] zoneNames = ["Tüm klavye", "Sol", "Orta", "Sağ"];
        for (int i = 0; i < 4; i++)
        {
            int zone = i - 1;
            _zones[i] = Segment("zone" + GetHashCode(), zoneNames[i]);
            _zones[i].Checked += (_, _) => { if (!_rendering) Pick(zone); };
            _zoneRow.Children.Add(_zones[i]);
        }

        _swatchRow = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var (name, hex) in Swatches) _swatchRow.Children.Add(Swatch(name, hex));
        _hex.SetResourceReference(Control.ForegroundProperty, "Text");
        _hex.SetResourceReference(Control.BackgroundProperty, "Card");
        _hex.SetResourceReference(Control.BorderBrushProperty, "Border");
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };
        _hex.LostFocus += (_, _) => ApplyHex();
        var custom = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0),
            Children = { InfoPage.Text("Özel renk", "Body"), _hex },
        };
        ((TextBlock)custom.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        _colourNote = InfoPage.Text("", "Caption", top: 6);

        _idle.SetResourceReference(StyleProperty, "ThemedCombo");
        _idle.ItemsSource = IdleChoices.Select(c => c.Label).ToList();
        _idle.SelectionChanged += (_, _) =>
        {
            if (_idle.SelectedIndex >= 0) Change(_light with { IdleOffMinutes = IdleChoices[_idle.SelectedIndex].Minutes });
        };

        _error = InfoPage.Text("", "Caption", top: 6);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        _error.Visibility = Visibility.Collapsed;

        Content = new StackPanel
        {
            Children =
            {
                InfoPage.Text("Aydınlatma", "PageTitle"),
                InfoPage.Text("Klavyenin üç bölgeli ışığı. Değişiklikler hemen uygulanır ve bilgisayar yeniden açılınca da korunur.", "Caption", top: 2, bottom: 18),
                InfoPage.Card("Klavye", _keyboard, _zoneRow, _swatchRow, custom, _colourNote),
                InfoPage.Card("Efekt", effects),
                InfoPage.Card("Parlaklık",
                    levels,
                    InfoPage.Text("Klavyedeki ışık tuşu da bu üç kademe arasında geçer; burada seçtiğiniz renkler korunur.", "Caption", top: 6)),
                InfoPage.Card("Boşta söndür",
                    InfoPage.Text("Klavyeye ve fareye bu süre dokunulmazsa ışık söner, dokununca hemen geri yanar. Simge alanındaki uygulama açıkken çalışır.", "Caption"),
                    _idle),
                _error,
            },
        };

        Loaded += async (_, _) =>
        {
            await _connection.RefreshSettingsAsync();
            if (_connection.Settings?.Lighting is { } l) _light = l;
            Render();
        };
        Render();
    }

    private RadioButton Segment(string group, object content, double minWidth = 96, double height = double.NaN)
    {
        var b = new RadioButton { GroupName = group, Content = content, Margin = new Thickness(0, 0, 8, 8), MinWidth = minWidth, Height = height };
        b.SetResourceReference(StyleProperty, "Segment");
        return b;
    }

    private Button Swatch(string name, string hex)
    {
        var (r, g, b) = KeyboardLighting.ParseColour(hex)!.Value;
        var button = new Button
        {
            Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, ToolTip = name,
            Template = SwatchTemplate(),
            Background = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b)),
        };
        button.SetResourceReference(BorderBrushProperty, "Border");
        button.Click += (_, _) => SetColour(hex);
        return button;
    }

    private static ControlTemplate SwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

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

    /// <summary>The colour goes to the picked zone, or to all three (also always with Breathing: it has one colour).</summary>
    private void SetColour(string hex)
    {
        Change(_zone switch
        {
            0 => _light with { Left = hex },
            1 => _light with { Middle = hex },
            2 => _light with { Right = hex },
            _ => _light with { Left = hex, Middle = hex, Right = hex },
        });
    }

    private async void Change(KeyboardLighting next)
    {
        if (_rendering || next == _light) return;
        var previous = _light;
        _light = next;
        if (next.Effect != LightEffect.Static) _zone = -1;
        Render();
        var r = await _connection.RequestAsync(new FanRequest("setLighting", Lighting: next));
        string? error = r is null ? "Fan servisine ulaşılamadı; ışık değiştirilemedi." : r.Ok ? null : "Işık ayarı kabul edilmedi.";
        if (error is not null && _light == next) { _light = previous; Render(); }
        _error.Text = error ?? "";
        _error.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Color ToColour(string hex) =>
        KeyboardLighting.ParseColour(hex) is var (r, g, b) ? Color.FromRgb((byte)r, (byte)g, (byte)b) : Colors.Black;

    private void Render()
    {
        _rendering = true;
        bool zoned = _light.Effect == LightEffect.Static, coloured = _light.Effect != LightEffect.ColorCycle;
        _keyboard.Colours = _light.Effect switch
        {
            LightEffect.Static => [ToColour(_light.Left), ToColour(_light.Middle), ToColour(_light.Right)],
            LightEffect.ColorCycle => [Color.FromRgb(255, 0, 128), Color.FromRgb(0, 200, 255), Color.FromRgb(255, 208, 0)],
            _ => [ToColour(_light.Left), ToColour(_light.Left), ToColour(_light.Left)],
        };
        _keyboard.Lit = _light.Brightness > 0;
        _keyboard.Selected = _zone;
        _keyboard.IsEnabled = zoned;
        _zoneRow.Visibility = zoned ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < 4; i++) _zones[i].IsChecked = _zone == i - 1;
        _swatchRow.IsEnabled = _hex.IsEnabled = coloured;
        string shown = _zone switch { 0 => _light.Left, 1 => _light.Middle, 2 => _light.Right, _ => _light.Left };
        if (!_hex.IsKeyboardFocused) _hex.Text = shown;
        _colourNote.Text = _light.Effect switch
        {
            LightEffect.Static => _zone < 0
                ? "Bir rengi seçince tüm klavyeye uygulanır. Tek bölgeyi değiştirmek için çizimde ona tıklayın."
                : "Seçtiğiniz renk yalnız bu bölgeye uygulanır. Komşu bölgelerin ışığı kenarlara biraz taşabilir.",
            LightEffect.Breathing => "Nefes efekti tüm klavyede tek renkle çalışır.",
            _ => "Renk döngüsü renkleri kendisi seçer.",
        };
        foreach (var (effect, button) in _effects) button.IsChecked = _light.Effect == effect;
        for (int i = 0; i < 3; i++) _levels[i].IsChecked = _light.Brightness == i;
        int idle = Array.FindIndex(IdleChoices, c => c.Minutes == _light.IdleOffMinutes);
        _idle.SelectedIndex = idle;
        _rendering = false;
    }
}
