using Excalibur.Core.Hardware;
using Excalibur.Core.Lighting;
using Excalibur.Core.Settings;

namespace Excalibur.Core.Tests;

public class KeyboardLightTests
{
    internal sealed class FakeSmi : ISmiChannel
    {
        public List<SmiCommand> Sent { get; } = [];
        public uint KeyBrightness { get; set; }

        public SmiCommand Execute(SmiCommand request)
        {
            Sent.Add(request);
            return new SmiCommand { A0 = request.A0, A1 = request.A1, A6 = KeyBrightness };
        }
    }

    private readonly FakeSmi _smi = new();
    private KeyboardLight Light() => new(_smi);

    [Theory]
    // The stock software's default profiles (docs/02 §3).
    [InlineData(LightEffect.ColorCycle, 1, 0xFF, 0x00, 0x80, 0x61FF0080u)]
    [InlineData(LightEffect.Static, 2, 0xFF, 0x00, 0x00, 0x12FF0000u)]
    [InlineData(LightEffect.Static, 1, 0x00, 0xFF, 0xFF, 0x1100FFFFu)]
    public void Encodes_like_the_stock_software(LightEffect effect, int brightness, int r, int g, int b, uint expected)
    {
        var s = new LightSetting(effect, brightness, r, g, b);
        Assert.Equal(expected, s.Encode());
        Assert.Equal(s, LightSetting.Decode(expected));
    }

    [Fact]
    public void Brightness_zero_sends_grey_like_the_stock_software()
        => Assert.Equal(0x10101010u, new LightSetting(LightEffect.Static, 0, 0, 255, 255).Encode());

    [Fact]
    public void Set_sends_the_zone_and_data_to_0x0100()
    {
        Light().Set(LightZone.KeyboardLeft, new LightSetting(LightEffect.Static, 2, 255, 0, 0));
        var c = Assert.Single(_smi.Sent);
        Assert.Equal(SmiCommand.OpSet, c.A0);
        Assert.Equal(0x0100, c.A1);
        Assert.Equal(5u, c.A2);
        Assert.Equal(0x12FF0000u, c.A3);
    }

    [Fact]
    public void The_same_command_twice_is_sent_once_until_forgotten()
    {
        var light = Light();
        var red = new LightSetting(LightEffect.Static, 2, 255, 0, 0);
        light.Set(LightZone.KeyboardLeft, red);
        light.Set(LightZone.KeyboardLeft, red);
        Assert.Single(_smi.Sent);
        light.Forget();
        light.Set(LightZone.KeyboardLeft, red);
        Assert.Equal(2, _smi.Sent.Count);
    }

    [Fact]
    public void A_whole_keyboard_command_invalidates_the_zones()
    {
        var light = Light();
        var red = new LightSetting(LightEffect.Static, 2, 255, 0, 0);
        light.Set(LightZone.KeyboardLeft, red);
        light.Set(LightZone.Keyboard, LightSetting.Off);
        light.Set(LightZone.KeyboardLeft, red);
        Assert.Equal(3, _smi.Sent.Count);
    }

    [Theory]
    [InlineData(LightEffect.Static, 3, 0)]
    [InlineData(LightEffect.Static, 1, 256)]
    [InlineData((LightEffect)7, 1, 0)]
    public void Invalid_settings_are_refused_and_nothing_is_sent(LightEffect effect, int brightness, int red)
    {
        Assert.Throws<ArgumentException>(() => Light().Set(LightZone.Keyboard, new LightSetting(effect, brightness, red, 0, 0)));
        Assert.Empty(_smi.Sent);
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(2u, 2)]
    [InlineData(3u, 2)]
    public void Key_brightness_comes_from_0x0200_a6(uint raw, int expected)
    {
        _smi.KeyBrightness = raw;
        Assert.Equal(expected, Light().ReadKeyBrightness());
    }
}

public class KeyboardLightingTests
{
    private static readonly KeyboardLighting Rgb = new() { Effect = LightEffect.Static, Brightness = 2, Left = "#FF0000", Middle = "#00FF00", Right = "#0000FF" };

    [Fact]
    public void Three_colours_go_to_the_three_zones_at_one_brightness()
    {
        var c = Rgb.Commands(idle: false);
        Assert.Equal([LightZone.KeyboardLeft, LightZone.KeyboardMiddle, LightZone.KeyboardRight], c.Select(x => x.Zone));
        Assert.All(c, x => Assert.Equal(2, x.Setting.Brightness));
        Assert.Equal(new LightSetting(LightEffect.Static, 2, 0, 0, 255), c[2].Setting);
    }

    [Fact]
    public void One_colour_is_one_whole_keyboard_command()
        => Assert.Equal(LightZone.Keyboard, Assert.Single(new KeyboardLighting { Left = "#123456", Middle = "#123456", Right = "#123456" }.Commands(false)).Zone);

    [Fact]
    public void Breathing_lights_the_whole_keyboard_in_the_left_colour()
    {
        var (zone, s) = Assert.Single((Rgb with { Effect = LightEffect.Breathing }).Commands(false));
        Assert.Equal(LightZone.Keyboard, zone);
        Assert.Equal(new LightSetting(LightEffect.Breathing, 2, 255, 0, 0), s);
    }

    [Fact]
    public void Idle_turns_the_keyboard_off_but_keeps_the_level()
    {
        // At level 0 the last command darkens the whole keyboard, and the light key would start from 0 (2026-10-23).
        var (zone, s) = Assert.Single(Rgb.Commands(idle: true));
        Assert.Equal(LightZone.Keyboard, zone);
        Assert.Equal(LightEffect.Off, s.Effect);
        Assert.Equal(2, s.Brightness);
    }

    [Theory]
    [InlineData("00FF00")]
    [InlineData("#00FF0")]
    [InlineData("#GG0000")]
    public void A_colour_that_is_not_rrggbb_is_refused(string colour)
        => Assert.NotEmpty((Rgb with { Middle = colour }).Validate());

    [Fact]
    public void Key_event_is_byte_1_zero_with_the_level_in_byte_0()
    {
        Assert.True(KeyboardLighting.TryParseKeyEvent([2, 0, 0, 0], out int b));
        Assert.Equal(2, b);
        Assert.False(KeyboardLighting.TryParseKeyEvent([0, 3, 0, 1], out _)); // Fn+1 cooling boost
    }

    [Fact]
    public void Lighting_survives_the_settings_file_and_a_bad_one_is_dropped()
    {
        var s = new ServiceSettings { Lighting = Rgb with { IdleOffMinutes = 5 } };
        Assert.Equal(s, ServiceSettings.Parse(s.ToJson()).Settings);
        var (bad, problems) = ServiceSettings.Parse("""{"lighting":{"brightness":7}}""");
        Assert.Null(bad.Lighting);
        Assert.NotEmpty(problems);
    }
}

public sealed class LightingManagerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "excalibur-light-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly KeyboardLightTests.FakeSmi _smi = new();
    private readonly ServiceSettingsStore _store;
    private readonly LightingManager _m;

    public LightingManagerTests()
    {
        _store = new ServiceSettingsStore(_path);
        _m = new LightingManager(new KeyboardLight(_smi), _store);
    }

    public void Dispose() => File.Delete(_path);

    private static readonly KeyboardLighting Cyan = new() { Brightness = 2, IdleOffMinutes = 5 };
    private LightSetting LastSent => LightSetting.Decode(_smi.Sent[^1].A3);

    [Fact]
    public void Nothing_is_written_until_the_user_chose_a_light()
    {
        _m.Reapply();
        _m.OnKey(2);
        _m.SetIdle(true);
        Assert.Empty(_smi.Sent);
    }

    [Fact]
    public void Set_stores_and_sends()
    {
        Assert.Empty(_m.Set(Cyan));
        Assert.Equal(Cyan, _store.Current.Lighting);
        Assert.Equal(LightEffect.Static, LastSent.Effect);
    }

    [Fact]
    public void An_invalid_light_is_refused_and_nothing_is_sent()
    {
        Assert.NotEmpty(_m.Set(Cyan with { Brightness = 5 }));
        Assert.Empty(_smi.Sent);
        Assert.Null(_store.Current.Lighting);
    }

    [Fact]
    public void The_light_key_level_is_kept_and_sent_with_the_colours()
    {
        _m.Set(Cyan);
        _m.OnKey(1);
        Assert.Equal(1, _store.Current.Lighting!.Brightness);
        Assert.Equal(new LightSetting(LightEffect.Static, 1, 0, 255, 255), LastSent);
    }

    [Fact]
    public void Idle_goes_dark_and_input_brings_the_light_back()
    {
        _m.Set(Cyan);
        _m.SetIdle(true);
        Assert.Equal(LightEffect.Off, LastSent.Effect);
        _m.SetIdle(false);
        Assert.Equal(LightEffect.Static, LastSent.Effect);
    }

    [Fact]
    public void Idle_is_ignored_when_idle_off_is_not_chosen()
    {
        _m.Set(Cyan with { IdleOffMinutes = 0 });
        int sent = _smi.Sent.Count;
        _m.SetIdle(true);
        Assert.Equal(sent, _smi.Sent.Count);
        Assert.False(_m.Idle);
    }

    [Fact]
    public void Reapply_sends_again_even_when_nothing_changed()
    {
        _m.Set(Cyan);
        int sent = _smi.Sent.Count;
        _m.Reapply();
        Assert.Equal(sent + 1, _smi.Sent.Count);
    }
}
