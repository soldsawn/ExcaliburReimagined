using Excalibur.Core.Settings;

namespace Excalibur.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+F", Hotkey.Control | Hotkey.Alt, 'F')]
    [InlineData("alt + shift + 7", Hotkey.Alt | Hotkey.Shift, '7')]
    [InlineData("Win+F12", Hotkey.Win, 0x7B)]
    public void Combinations_are_read(string text, uint mods, uint key)
        => Assert.Equal(new Hotkey(mods, key), Hotkey.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("F")]            // no modifier
    [InlineData("Shift+F")]      // Shift alone types a capital letter
    [InlineData("Ctrl+Alt")]     // no key
    [InlineData("Ctrl+F+G")]     // two keys
    [InlineData("Ctrl+Space")]   // not offered
    [InlineData("Ctrl+F25")]
    public void Other_text_is_not_a_shortcut(string text) => Assert.Null(Hotkey.Parse(text));

    [Fact]
    public void Text_is_written_in_one_order()
    {
        Assert.Equal("Ctrl+Alt+Shift+F3", Hotkey.Parse("shift+F3+alt+ctrl")!.Value.ToString());
        Assert.Equal("", Hotkey.Clean("Shift+F"));
    }

    [Fact]
    public void Settings_keep_the_defaults_and_drop_bad_entries()
    {
        var s = UserSettings.Parse("""{ "hotkeys": { "enabled": false, "profile": "ctrl+alt+p", "powerMode": "nonsense" } }""");
        Assert.False(s.Hotkeys.Enabled);
        Assert.Equal("Ctrl+Alt+P", s.Hotkeys.Profile);
        Assert.Equal("", s.Hotkeys.PowerMode);
        Assert.Equal("Ctrl+Alt+E", s.Hotkeys.Window);
        Assert.Equal(new HotkeySettings(), UserSettings.Parse("{}").Hotkeys);
    }
}
