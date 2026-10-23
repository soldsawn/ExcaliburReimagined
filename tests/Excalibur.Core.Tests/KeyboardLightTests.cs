using Excalibur.Core.Hardware;
using Excalibur.Core.Lighting;

namespace Excalibur.Core.Tests;

public class KeyboardLightTests
{
    private sealed class FakeSmi : ISmiChannel
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
