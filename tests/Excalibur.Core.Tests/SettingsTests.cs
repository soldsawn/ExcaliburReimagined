using System.Text.Json;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.Settings;
using static Excalibur.Core.Tests.FanTestData;

namespace Excalibur.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "excalibur-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string name) => Path.Combine(_dir, name);

    // ---- ServiceSettings parsing ----------------------------------------------------------

    [Fact]
    public void V1_file_is_migrated_without_complaint()
    {
        var (s, problems) = ServiceSettings.Parse("""{"Profile":"Quiet"}""");
        Assert.Equal("Quiet", s.Profile);
        Assert.Equal(HistoryMode.Days7, s.History);
        Assert.Equal(ServiceSettings.CurrentVersion, s.Version);
        Assert.Empty(problems);
    }

    [Fact]
    public void Profile_names_are_matched_case_insensitively_and_normalized()
    {
        var (s, _) = ServiceSettings.Parse("""{"profile":"performance"}""");
        Assert.Equal("Performance", s.Profile);
    }

    [Theory]
    [InlineData("""{"profile":"Turbo"}""")]
    [InlineData("""{"profile":42}""")]
    [InlineData("""{"version":2,"profile":"Quiet","history":"Forever"}""")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Bad_values_fall_back_to_defaults_and_are_reported(string json)
    {
        var (s, problems) = ServiceSettings.Parse(json);
        Assert.Empty(s.Validate());
        Assert.NotEmpty(problems);
    }

    [Fact]
    public void A_newer_version_keeps_known_fields_and_says_so()
    {
        var (s, problems) = ServiceSettings.Parse("""{"version":9,"profile":"Quiet","history":"Off","lighting":{"zone":1}}""");
        Assert.Equal("Quiet", s.Profile);
        Assert.Equal(HistoryMode.Off, s.History);
        Assert.Contains(problems, p => p.Contains("newer"));
    }

    [Fact]
    public void Settings_round_trip_through_json()
    {
        var original = new ServiceSettings { Profile = "Performance", History = HistoryMode.Hour1 };
        var (back, problems) = ServiceSettings.Parse(original.ToJson());
        Assert.Equal(original, back);
        Assert.Empty(problems);
    }

    [Fact]
    public void Trusted_ec_versions_round_trip_and_bad_ones_are_dropped()
    {
        var (s, problems) = ServiceSettings.Parse("""{"trustedEcVersions":["P009C0","bad",42,"P009C0"],"wmiFanEcVersions":["P009C0","x"]}""");
        Assert.Equal(["P009C0"], s.TrustedEcVersions);
        Assert.Equal(["P009C0"], s.WmiFanEcVersions);
        Assert.Equal(3, problems.Count);
        var (back, again) = ServiceSettings.Parse(s.ToJson());
        Assert.Equal(s, back);
        Assert.Empty(again);
    }

    [Fact]
    public void A_patch_changes_only_what_it_names()
    {
        var s = new ServiceSettings { Profile = "Quiet", History = HistoryMode.Days7 };
        Assert.Equal(s with { History = HistoryMode.Off }, new ServiceSettingsPatch(HistoryMode.Off).ApplyTo(s));
        Assert.Equal(s, new ServiceSettingsPatch().ApplyTo(s));
    }

    // ---- ServiceSettingsStore -------------------------------------------------------------

    [Fact]
    public void Missing_file_gives_defaults_without_problems()
    {
        var store = new ServiceSettingsStore(File("none.json"));
        Assert.Equal(new ServiceSettings(), store.Current);
        Assert.Empty(store.LoadProblems);
    }

    [Fact]
    public void Update_saves_atomically_and_raises_changed()
    {
        string path = File("fan.json");
        var store = new ServiceSettingsStore(path);
        ServiceSettings? seen = null;
        store.Changed += s => seen = s;

        var errors = store.Update(s => s with { Profile = "Quiet" }, out bool saved);

        Assert.Empty(errors);
        Assert.True(saved);
        Assert.False(System.IO.File.Exists(path + ".tmp"));
        Assert.Equal("Quiet", seen?.Profile);
        Assert.Equal("Quiet", new ServiceSettingsStore(path).Current.Profile);
    }

    [Fact]
    public void Invalid_update_is_rejected_and_leaves_the_file_alone()
    {
        string path = File("fan.json");
        var store = new ServiceSettingsStore(path);
        store.Update(s => s with { Profile = "Quiet" }, out _);
        string before = System.IO.File.ReadAllText(path);
        bool raised = false;
        store.Changed += _ => raised = true;

        var errors = store.Update(s => s with { Profile = "Turbo" }, out bool saved);

        Assert.NotEmpty(errors);
        Assert.False(saved);
        Assert.False(raised);
        Assert.Equal("Quiet", store.Current.Profile);
        Assert.Equal(before, System.IO.File.ReadAllText(path));
    }

    [Fact]
    public void An_existing_v1_file_is_rewritten_as_v2_on_the_next_update()
    {
        string path = File("fan.json");
        System.IO.File.WriteAllText(path, """{"Profile":"EcAuto"}""");
        var store = new ServiceSettingsStore(path);
        Assert.Equal("EcAuto", store.Current.Profile);

        store.Update(s => s with { History = HistoryMode.Off }, out _);

        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        Assert.Equal(ServiceSettings.CurrentVersion, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("EcAuto", doc.RootElement.GetProperty("profile").GetString());
        Assert.Equal("Off", doc.RootElement.GetProperty("history").GetString());
    }

    // ---- UserSettings ---------------------------------------------------------------------

    [Theory]
    [InlineData("""{"accent":"red"}""")]
    [InlineData("""{"accent":"#12345"}""")]
    [InlineData("""{"trayFont":99}""")]
    [InlineData("garbage")]
    public void User_settings_fall_back_per_field(string json)
    {
        var s = UserSettings.Parse(json);
        Assert.True(UserSettings.IsColor(s.Accent));
        Assert.True(Enum.IsDefined(s.TrayFont));
    }

    [Fact]
    public void User_settings_default_to_bahnschrift_osd_on_red_accent()
    {
        var s = new UserSettings();
        Assert.Equal(TrayFont.Bahnschrift, s.TrayFont);
        Assert.True(s.Osd);
        Assert.Equal(UserSettings.DefaultAccent, s.Accent);
    }

    [Fact]
    public void Appearance_extras_default_on_and_survive_a_round_trip()
    {
        var d = new UserSettings();
        Assert.True(d.CornerMarks);
        Assert.True(d.Motion);
        Assert.Equal(NumberFont.Bahnschrift, d.NumberFont);

        var s = d with { CornerMarks = false, Motion = false, NumberFont = NumberFont.SegoeUi, Theme = ThemeMode.Light, Accent = "#2F6FE0" };
        Assert.Equal(s, UserSettings.Parse(JsonSerializer.Serialize(s, UserSettings.Json)));
        Assert.NotEqual(d, s with { Accent = d.Accent, Theme = d.Theme, CornerMarks = true, Motion = true });
        Assert.Equal(NumberFont.Bahnschrift, UserSettings.Parse("""{"numberFont":42}""").NumberFont);
    }

    [Fact]
    public void An_accent_keeps_its_hue_and_reads_on_both_themes()
    {
        // The default red and the presets are fine on a dark theme as they are.
        foreach (var (hex, _) in AccentColor.Presets)
            Assert.Equal(AccentColor.Parse(hex), AccentColor.Readable(hex, dark: true));
        // Near-black and near-white are pulled into range; a gray stays gray.
        Assert.Equal((102, 102, 102), AccentColor.Readable("#050505", dark: true));
        Assert.Equal((115, 115, 115), AccentColor.Readable("#FAFAFA", dark: false));
        // The default red comes out darker on a light theme, close to its former light red (196, 20, 42).
        var (r, g, b) = AccentColor.Readable(UserSettings.DefaultAccent, dark: false);
        Assert.InRange(r, 190, 205);
        Assert.InRange(g, 15, 35);
        Assert.InRange(b, 35, 55);
        Assert.Equal(AccentColor.Parse(UserSettings.DefaultAccent), AccentColor.Readable("not a colour", dark: true));
    }

    [Theory]
    [InlineData("""{"refreshRate":{"enabled":true,"onAcHz":10,"onBatteryHz":60}}""")]
    [InlineData("""{"refreshRate":{"enabled":true,"onAcHz":144,"onBatteryHz":0}}""")]
    public void An_unusable_refresh_rule_falls_back_to_the_default(string json)
    {
        var s = UserSettings.Parse(json);
        Assert.False(s.RefreshRate.Enabled);
        Assert.Equal(60, s.RefreshRate.OnBatteryHz);
    }

    [Fact]
    public void A_valid_refresh_rule_is_kept()
    {
        var s = UserSettings.Parse("""{"refreshRate":{"enabled":true,"onAcHz":0,"onBatteryHz":60}}""");
        Assert.True(s.RefreshRate.Enabled);
        Assert.Equal(0, s.RefreshRate.OnAcHz);
    }

    [Fact]
    public void User_settings_store_round_trips()
    {
        string path = File("settings.json");
        using (var store = new UserSettingsStore(path))
            Assert.True(store.Save(new UserSettings { TrayFont = TrayFont.PixelBold, Osd = false, Accent = "#00FF88" }));
        using var again = new UserSettingsStore(path);
        Assert.Equal(TrayFont.PixelBold, again.Current.TrayFont);
        Assert.False(again.Current.Osd);
        Assert.Equal("#00FF88", again.Current.Accent);
    }

    // ---- protocol v2 ----------------------------------------------------------------------

    [Fact]
    public void Profile_changes_report_who_made_them()
    {
        var loop = new FanControlLoop(new FakeFanHardware());
        loop.Start(FanProfile.Balanced, T0);
        loop.SetProfile(FanProfile.Quiet, T0.AddSeconds(1), FanSource.Window);
        Assert.Equal(FanSource.Window, loop.Status.ProfileChangedBy);
        Assert.Equal(T0.AddSeconds(1), loop.Status.ProfileChangedAt);

        loop.SetManual(70, 60, TimeSpan.FromSeconds(30), T0.AddSeconds(2), FanSource.Tray);
        Assert.Equal(FanSource.Tray, loop.Status.ProfileChangedBy);

        loop.Tick(T0.AddSeconds(40)); // manual mode runs out
        Assert.Equal("Quiet", loop.Status.Profile);
        Assert.Equal(FanSource.Service, loop.Status.ProfileChangedBy);
    }

    [Fact]
    public void A_v1_status_still_deserializes_and_a_v2_request_round_trips()
    {
        const string v1 = """{"mode":"Running","profile":"Balanced","coolingBoost":false,"consecutiveReadFailures":0,"holdsFans":true,"simulated":false,"serviceStartedAt":"2026-10-20T12:00:00+00:00"}""";
        var status = JsonSerializer.Deserialize<FanStatusDto>(v1, FanPipe.Json)!;
        Assert.Equal(1, status.ProtocolVersion);
        Assert.Null(status.ProfileChangedBy);

        var request = new FanRequest("updateSettings", Source: FanSource.Window, Settings: new ServiceSettingsPatch(HistoryMode.Hour1));
        string json = JsonSerializer.Serialize(request, FanPipe.Json);
        Assert.Contains("\"Hour1\"", json);
        Assert.Equal(request, JsonSerializer.Deserialize<FanRequest>(json, FanPipe.Json));
    }
    [Fact]
    public void A_cpu_limit_survives_the_file_and_a_bad_one_is_dropped()
    {
        var saved = new ServiceSettings { CpuLimit = new Excalibur.Core.Power.CpuLimit(25, 35) };
        Assert.Equal(saved, ServiceSettings.Parse(saved.ToJson()).Settings);

        var (bad, problems) = ServiceSettings.Parse("""{"cpuLimit":{"pl1Watts":5,"pl2Watts":35}}""");
        Assert.Null(bad.CpuLimit);
        Assert.Single(problems);
    }

    [Fact]
    public void A_gpu_limit_survives_the_file_and_a_bad_one_is_dropped()
    {
        var saved = new ServiceSettings { GpuLimitWatts = 45 };
        Assert.Equal(saved, ServiceSettings.Parse(saved.ToJson()).Settings);

        var (bad, problems) = ServiceSettings.Parse("""{"gpuLimitWatts":12}""");
        Assert.Null(bad.GpuLimitWatts);
        Assert.Single(problems);
    }
}
