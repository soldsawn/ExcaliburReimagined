using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Excalibur.Core.Settings;

namespace Excalibur.App.Ui.Pages;

/// <summary>
/// Settings' shortcut card (docs/08 5.2): on/off and one combination each for the fan profile, the power mode and the window.
/// Saved to the user settings file; the tray registers them with Windows and says so if another program holds one.
/// </summary>
internal static class HotkeysCard
{
    private static readonly (string Label, string Explanation, Func<HotkeySettings, string> Get, Func<HotkeySettings, string, HotkeySettings> Set)[] Rows =
    [
        ("Fan profilini değiştir", "Hızlı erişimdeki profiller sırayla seçilir.", h => h.Profile, (h, v) => h with { Profile = v }),
        ("Güç modunu değiştir", "Ofis, Oyun ve Yüksek performans sırayla; pilde yalnız Ofis.", h => h.PowerMode, (h, v) => h with { PowerMode = v }),
        ("Pencereyi aç", "Excalibur Reimagined penceresini açar ya da öne getirir.", h => h.Window, (h, v) => h with { Window = v }),
    ];

    public static Border Create()
    {
        var store = new UserSettingsStore();
        HotkeySettings Current() => store.Current.Hotkeys;
        void Save(HotkeySettings h) => store.Save(store.Current with { Hotkeys = h });

        var error = InfoPage.Text("", "Caption", top: 8);
        error.Visibility = Visibility.Collapsed;
        void ShowError(string? text)
        {
            error.Text = text ?? "";
            error.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }

        var enabled = new CheckBox { Content = "Kısayol tuşlarını kullan", FontSize = 14, IsChecked = Current().Enabled, Margin = new Thickness(0, 0, 0, 10) };
        enabled.SetResourceReference(Control.ForegroundProperty, "Text");
        enabled.Click += (_, _) => Save(Current() with { Enabled = enabled.IsChecked == true });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < Rows.Length; i++)
        {
            var row = Rows[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = InfoPage.Labeled(row.Label, "Body", row.Explanation);
            label.VerticalAlignment = VerticalAlignment.Center;
            var button = KeyButton(() => row.Get(Current()), text =>
            {
                // One combination for one job.
                if (text.Length > 0 && Rows.Where(r => r.Label != row.Label).Any(r => r.Get(Current()) == text))
                    return $"{text} başka bir iş için kullanılıyor.";
                Save(row.Set(Current(), text));
                return null;
            }, ShowError);
            label.Margin = button.Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 0);
            Grid.SetRow(label, i);
            Grid.SetRow(button, i);
            Grid.SetColumn(button, 1);
            grid.Children.Add(label);
            grid.Children.Add(button);
        }

        return InfoPage.Card("Kısayol tuşları",
            InfoPage.Text("Bilgisayarın her yerinde çalışır. Değiştirmek için düğmeye tıklayıp yeni birleşime basın: Ctrl, Alt ya da Win ile " +
                          "birlikte bir harf, rakam ya da F tuşu. Delete kaldırır, Esc vazgeçer.", "Caption", bottom: 12),
            enabled, grid, error);
    }

    /// <summary>A button that shows the combination and, once clicked, takes the next key press as the new one.</summary>
    private static Button KeyButton(Func<string> current, Func<string, string?> chosen, Action<string?> showError)
    {
        static string Show(string text) => text.Length == 0 ? "Yok" : text;
        var button = new Button { Content = Show(current()), MinWidth = 170, HorizontalAlignment = HorizontalAlignment.Left };
        button.SetResourceReference(FrameworkElement.StyleProperty, "PlainButton");
        bool listening = false;
        void Stop() { listening = false; button.Content = Show(current()); }
        button.Click += (_, _) => { listening = true; showError(null); button.Content = "Tuşlara basın…"; };
        button.LostKeyboardFocus += (_, _) => { if (listening) Stop(); };
        button.PreviewKeyDown += (_, e) =>
        {
            if (!listening) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { Stop(); return; }
            if (key is Key.Back or Key.Delete)
            {
                showError(chosen(""));
                Stop();
                return;
            }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                return; // wait for the key itself
            string? name = key switch
            {
                >= Key.A and <= Key.Z => key.ToString(),
                >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
                >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
                >= Key.F1 and <= Key.F24 => $"F{key - Key.F1 + 1}",
                _ => null,
            };
            var mods = Keyboard.Modifiers;
            bool win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
            string text = string.Join("+", new[]
            {
                (mods & ModifierKeys.Control) != 0 ? "Ctrl" : null,
                (mods & ModifierKeys.Alt) != 0 ? "Alt" : null,
                (mods & ModifierKeys.Shift) != 0 ? "Shift" : null,
                win ? "Win" : null,
                name,
            }.Where(p => p is not null));
            if (name is null || Hotkey.Parse(text) is not { } parsed)
            {
                showError("Ctrl, Alt ya da Win ile birlikte bir harf, rakam ya da F tuşuna basın.");
                return;
            }
            showError(chosen(parsed.ToString()));
            Stop();
        };
        return button;
    }
}
