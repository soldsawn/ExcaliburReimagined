using Excalibur.Core.Fan;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.Core.Tests;

public sealed class CustomProfileTests
{
    private static CurvePoint P(int t, int p) => new(t, p);

    private static CustomProfile Valid(string name = "Gece") =>
        new(name, [P(40, 30), P(60, 50), P(80, 100)]);

    // ---- validation ------------------------------------------------------------------------

    [Fact]
    public void A_sensible_profile_is_valid_and_runs()
    {
        var c = Valid();
        Assert.Empty(c.Validate());
        var profile = c.ToProfile();
        Assert.Equal("Gece", profile.Name);
        Assert.Equal(50, profile.CpuCurve.Evaluate(60));
        Assert.Same(profile.CpuCurve, profile.GpuCurve); // no GPU curve = the same one
    }

    [Fact]
    public void A_separate_gpu_curve_is_used()
    {
        var c = Valid() with { Gpu = [P(40, 60), P(70, 100)] };
        Assert.Empty(c.Validate());
        Assert.Equal(60, c.ToProfile().GpuCurve.Evaluate(30));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Quiet")]
    [InlineData("balanced")]
    [InlineData("Sessiz")]
    [InlineData("Orijinal ayar")]
    [InlineData("EcAuto")]
    [InlineData("Manual")]
    [InlineData("Bu isim yirmi dört karakterden uzun")]
    public void Bad_names_are_rejected(string name) => Assert.NotEmpty(Valid(name).Validate());

    [Fact]
    public void Percent_below_the_absolute_floor_is_rejected()
    {
        var c = new CustomProfile("Sessiz gece", [P(40, 10), P(80, 100)]);
        var errors = c.Validate();
        Assert.Contains(errors, e => e.Contains("25"));
    }

    [Theory]
    [InlineData(40, 50, 40, 60)]   // temperature does not increase
    [InlineData(40, 60, 50, 50)]   // percent drops as it gets hotter
    [InlineData(40, 50, 50, 101)]  // above 100
    [InlineData(10, 50, 50, 60)]   // below the allowed temperature range
    [InlineData(40, 50, 120, 60)]  // above the allowed temperature range
    public void Broken_curves_are_rejected(int t1, int p1, int t2, int p2) =>
        Assert.NotEmpty(new CustomProfile("Deneme", [P(t1, p1), P(t2, p2)]).Validate());

    [Fact]
    public void Empty_and_oversized_curves_are_rejected()
    {
        Assert.NotEmpty(new CustomProfile("Boş", []).Validate());
        var many = Enumerable.Range(0, CustomProfile.MaxPoints + 1).Select(i => P(30 + i * 5, 30 + i * 4)).ToList();
        Assert.NotEmpty(new CustomProfile("Çok", many).Validate());
    }

    [Fact]
    public void A_gpu_curve_equal_to_the_cpu_one_is_stored_as_null()
    {
        var c = (Valid() with { Gpu = [P(40, 30), P(60, 50), P(80, 100)] }).Normalized();
        Assert.Null(c.Gpu);
    }

    // ---- settings --------------------------------------------------------------------------

    [Fact]
    public void Settings_with_custom_profiles_survive_a_round_trip()
    {
        var s = new ServiceSettings { CustomProfiles = [Valid(), Valid("Oyun") with { Gpu = [P(40, 60), P(70, 100)] }], Profile = "Oyun" };
        Assert.Empty(s.Validate());
        var (back, problems) = ServiceSettings.Parse(s.ToJson());
        Assert.Empty(problems);
        Assert.Equal("Oyun", back.Profile);
        Assert.Equal(2, back.CustomProfiles.Count);
        Assert.Equal(s.CustomProfiles[1].Gpu, back.CustomProfiles[1].Gpu);
        Assert.NotNull(back.FindProfile("oyun"));
    }

    [Fact]
    public void A_bad_custom_profile_in_the_file_is_dropped_and_the_rest_kept()
    {
        var json = """
            {"version":2,"profile":"Gece","customProfiles":[
              {"name":"Gece","cpu":[{"tempC":40,"percent":30},{"tempC":80,"percent":100}]},
              {"name":"Kırık","cpu":[{"tempC":40,"percent":90},{"tempC":80,"percent":30}]},
              {"name":"Quiet","cpu":[{"tempC":40,"percent":30}]}]}
            """;
        var (s, problems) = ServiceSettings.Parse(json);
        Assert.Single(s.CustomProfiles);
        Assert.Equal("Gece", s.Profile);
        Assert.Equal(2, problems.Count);
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void A_missing_active_profile_falls_back_to_balanced()
    {
        var (s, problems) = ServiceSettings.Parse("""{"profile":"Gece","customProfiles":[]}""");
        Assert.Equal("Balanced", s.Profile);
        Assert.NotEmpty(problems);
    }

    [Fact]
    public void Slots_default_to_the_four_built_ins_and_are_validated()
    {
        var s = new ServiceSettings { CustomProfiles = [Valid()] };
        Assert.Equal(ServiceSettings.DefaultSlots, s.EffectiveSlots);
        Assert.Empty((s with { Slots = ["Gece", "Quiet"] }).Validate());
        Assert.NotEmpty((s with { Slots = ["Gece", "Gece"] }).Validate());
        Assert.NotEmpty((s with { Slots = ["Yok"] }).Validate());
        Assert.NotEmpty((s with { Slots = ["Quiet", "Balanced", "Performance", "EcAuto", "Gece"] }).Validate());
        Assert.NotEmpty((s with { Slots = [] }).Validate());
    }

    [Fact]
    public void Two_custom_profiles_cannot_share_a_name()
    {
        var s = new ServiceSettings { CustomProfiles = [Valid("Gece"), Valid("gece")] };
        Assert.NotEmpty(s.Validate());
    }

    [Fact]
    public void An_unknown_profile_in_an_automation_rule_is_reported()
    {
        var s = new ServiceSettings { PowerAutomation = new PowerAutomation { OnAc = new PowerRule { FanProfile = "Gece" } } };
        Assert.NotEmpty(s.Validate());
        var ok = new ServiceSettings { CustomProfiles = [Valid()], PowerAutomation = new PowerAutomation { OnAc = new PowerRule { FanProfile = "Gece" } } };
        Assert.Empty(ok.Validate());
    }

    // ---- library operations ----------------------------------------------------------------

    [Fact]
    public void Save_adds_then_replaces_in_place()
    {
        var s = ProfileLibrary.Save(new ServiceSettings(), Valid("A"));
        s = ProfileLibrary.Save(s, Valid("B"));
        s = ProfileLibrary.Save(s, new CustomProfile("a", [P(40, 40), P(80, 100)]));
        Assert.Equal(["a", "B"], s.CustomProfiles.Select(c => c.Name));
        Assert.Equal(40, s.CustomProfiles[0].Cpu[0].Percent);
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void Renaming_the_capitalisation_follows_into_the_active_profile_and_slots()
    {
        var s = ProfileLibrary.Save(new ServiceSettings(), Valid("gece")) with { Profile = "gece", Slots = ["gece"] };
        s = ProfileLibrary.Save(s, Valid("Gece"));
        Assert.Equal("Gece", s.Profile);
        Assert.Equal(["Gece"], s.Slots);
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void Delete_cleans_up_everything_that_pointed_at_the_profile()
    {
        var s = ProfileLibrary.Save(new ServiceSettings(), Valid()) with
        {
            Profile = "Gece",
            Slots = ["Gece", "Quiet"],
            PowerAutomation = new PowerAutomation { OnAc = new PowerRule { Mode = PowerMode.Gaming, FanProfile = "Gece" }, OnBattery = new PowerRule { Mode = PowerMode.Office, FanProfile = "Quiet" } },
        };
        Assert.Empty(s.Validate());

        var after = ProfileLibrary.Delete(s, "gece");
        Assert.Empty(after.CustomProfiles);
        Assert.Equal("Balanced", after.Profile);
        Assert.Equal(["Quiet"], after.Slots);
        Assert.Null(after.PowerAutomation.OnAc.FanProfile);
        Assert.Equal(PowerMode.Gaming, after.PowerAutomation.OnAc.Mode);
        Assert.Equal("Quiet", after.PowerAutomation.OnBattery.FanProfile);
        Assert.Empty(after.Validate());
    }

    [Fact]
    public void Deleting_the_last_slot_returns_to_the_default_four()
    {
        var s = ProfileLibrary.Save(new ServiceSettings(), Valid()) with { Slots = ["Gece"] };
        Assert.Null(ProfileLibrary.Delete(s, "Gece").Slots);
    }

    [Fact]
    public void SetSlots_uses_the_stored_spelling()
    {
        var s = ProfileLibrary.Save(new ServiceSettings(), Valid());
        var next = ProfileLibrary.SetSlots(s, ["gece", "quiet"]);
        Assert.Equal(["Gece", "Quiet"], next.Slots);
        Assert.Empty(next.Validate());
    }

    [Fact]
    public void Too_many_profiles_are_rejected_by_validation()
    {
        var s = new ServiceSettings
        {
            CustomProfiles = Enumerable.Range(0, CustomProfile.MaxProfiles + 1).Select(i => Valid("P" + i)).ToList(),
        };
        Assert.NotEmpty(s.Validate());
    }
}
