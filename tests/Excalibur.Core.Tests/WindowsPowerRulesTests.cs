using Excalibur.Core.Power;
using Excalibur.Core.Settings;
using Xunit;

namespace Excalibur.Core.Tests;

public class WindowsPowerRulesTests
{
    [Fact]
    public void Off_by_default() => Assert.False(new UserSettings().WindowsPower.Enabled);

    [Fact]
    public void A_saved_rule_round_trips()
    {
        var plan = Guid.Parse("e6d624bb-e749-4e5e-a719-ffdba4450d67");
        var s = new UserSettings
        {
            WindowsPower = new WindowsPowerRules { Enabled = true, HighPerformance = new WindowsPowerRule(plan, null) },
        };
        var back = UserSettings.Parse(System.Text.Json.JsonSerializer.Serialize(s, UserSettings.Json));
        Assert.Equal(s.WindowsPower, back.WindowsPower);
    }

    [Fact]
    public void An_unknown_power_mode_falls_back_to_the_defaults()
    {
        var s = UserSettings.Parse("""{"windowsPower":{"enabled":true,"gaming":{"mode":7}}}""");
        Assert.Equal(new WindowsPowerRules(), s.WindowsPower);
    }

    [Theory]
    [InlineData(PowerMode.Gaming, false, WindowsPowerOverlay.Balanced)]
    [InlineData(PowerMode.HighPerformance, false, WindowsPowerOverlay.BestPerformance)]
    [InlineData(PowerMode.Office, true, WindowsPowerOverlay.BestEfficiency)]
    public void The_battery_has_its_own_rule(PowerMode mode, bool onBattery, WindowsPowerOverlay expected)
    {
        var rules = new WindowsPowerRules { Battery = new WindowsPowerRule(Mode: WindowsPowerOverlay.BestEfficiency), Office = new() };
        Assert.Equal(expected, rules.RuleFor(mode, onBattery).Mode);
    }
}
