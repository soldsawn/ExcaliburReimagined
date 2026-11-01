using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Lighting;
using Excalibur.Linux.Ui.Controls;

namespace Excalibur.Linux.Ui.Pages;

/// <summary>
/// Aydınlatma, as on Windows: the backlit keyboard on top (a click picks a zone), the light settings on the left and the
/// colour wheel on the right. Every change is sent at once (setLighting); the service keeps it and shows it again after a
/// restart or sleep. Brightness follows the light key (status LightBrightness). Windows' "turn off when idle" needs the tray
/// to notice idle input, which the Linux version does not do yet, so it is not offered here.
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

    private readonly FanConnection _connection;
    private readonly KeyboardView _keyboard = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly ColourWheel _wheel = new() { Margin = new Thickness(0, 0, 18, 0) };
    private readonly Dictionary<LightEffect, RadioButton> _effects = [];
    private readonly RadioButton[] _levels = new RadioButton[3];
    private readonly RadioButton[] _zones = new RadioButton[4];
    private readonly StackPanel _zoneRow;
    private readonly Control _colourPanel;
    private readonly TextBox _hex = new() { Width = 96, Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0) };
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
            var b = Segment("zone" + GetHashCode(), zoneNames[i]);
            b.IsCheckedChanged += (_, _) => { if (!_rendering && b.IsChecked == true) Pick(zone); };
            _zones[i] = b;
            _zoneRow.Children.Add(b);
        }

        // ---- left: effect, brightness ----
        var effects = new WrapPanel();
        foreach (var (effect, label) in Effects)
        {
            var b = Segment("effect" + GetHashCode(), label);
            b.IsCheckedChanged += (_, _) => { if (b.IsChecked == true) Change(_light with { Effect = effect }); };
            _effects[effect] = b;
            effects.Children.Add(b);
        }
        _effectNote = Kit.Text("", "Caption", top: 2, bottom: 12);
        var levels = new WrapPanel();
        for (int i = 0; i < Levels.Length; i++)
        {
            int level = i;
            _levels[i] = Segment("level" + GetHashCode(), Levels[i]);
            // A click on the level already shown still sends: the light key may have changed it a moment ago.
            _levels[i].Click += (_, _) => Change(_light with { Brightness = level }, force: true);
            levels.Children.Add(_levels[i]);
        }
        var settingsCard = Kit.Card("Işık",
            Label("Efekt"), effects, _effectNote,
            Label("Parlaklık", "Klavyedeki ışık tuşu da bu üç kademe arasında geçer; seçtiğiniz renkler korunur."), levels);

        // ---- right: colour ----
        _wheel.Picking += Preview;
        _wheel.Picked += c => SetColour(Hex(c));
        var swatches = new WrapPanel { Width = 5 * 34 };
        foreach (var (name, hex) in Swatches) swatches.Children.Add(Swatch(name, hex));
        _hex.Res(TextBox.BackgroundProperty, "Bg").Res(TextBox.FontFamilyProperty, "MonoFont");
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };
        _hex.LostFocus += (_, _) => ApplyHex();
        _colourNote = Kit.Text("", "Caption", top: 10);
        _colourNote.MaxWidth = 5 * 34;
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { _hex } };
        var colourSide = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { swatches, hexRow, _colourNote } };
        var colourBody = new StackPanel { Orientation = Orientation.Horizontal, Children = { _wheel, colourSide } };
        _colourPanel = colourBody;
        var colourCard = Kit.Card("Renk", colourBody);
        Grid.SetColumn(colourCard, 2);
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,Auto"), Children = { settingsCard, colourCard } };

        _error = Kit.Text("", "Caption", top: 2).Res(TextBlock.ForegroundProperty, "Bad");
        _error.IsVisible = false;

        Content = new StackPanel
        {
            Children =
            {
                Kit.Text("Aydınlatma", "PageTitle"),
                Kit.Text("Klavyenin üç bölgeli ışığı. Değişiklikler hemen uygulanır ve bilgisayar yeniden açılınca da korunur.", "Caption", top: 2, bottom: 18),
                Kit.Card("Klavye", _keyboard, _zoneRow),
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
        AttachedToVisualTree += async (_, _) =>
        {
            await _connection.RefreshSettingsAsync();
            if (_connection.Settings?.Lighting is { } l) _light = l;
            Render();
        };
        Render();
    }

    // ---- building blocks -------------------------------------------------------------------

    private static Control Label(string text, string? tip = null) =>
        tip is null ? Kit.Text(text, "Kicker", bottom: 6) : Kit.Labeled(text, "Kicker", tip, top: 4, bottom: 6);

    private static RadioButton Segment(string group, string text)
    {
        var b = Kit.Segment(text, group);
        b.Margin = new Thickness(0, 0, 6, 6);
        return b;
    }

    private Control Swatch(string name, string hex)
    {
        var swatch = new Border
        {
            Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 8), Cursor = new Cursor(StandardCursorType.Hand),
            CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(2), Background = new SolidColorBrush(ToColour(hex)),
        }.Res(Border.BorderBrushProperty, "Border");
        ToolTip.SetTip(swatch, name);
        swatch.PointerReleased += (_, _) => SetColour(hex);
        return swatch;
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
        string text = (_hex.Text ?? "").Trim().ToUpperInvariant();
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
        _error.IsVisible = error is not null;
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
        _zoneRow.IsVisible = zoned;
        for (int i = 0; i < 4; i++) _zones[i].IsChecked = _zone == i - 1;

        _colourPanel.IsEnabled = _light.Effect != LightEffect.ColorCycle;
        _colourPanel.Opacity = _colourPanel.IsEnabled ? 1 : 0.4;
        string shown = _zone switch { 0 => _light.Left, 1 => _light.Middle, 2 => _light.Right, _ => _light.Left };
        _wheel.Colour = ToColour(shown);
        if (!_hex.IsFocused) _hex.Text = shown;
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
        _rendering = false;
    }
}
