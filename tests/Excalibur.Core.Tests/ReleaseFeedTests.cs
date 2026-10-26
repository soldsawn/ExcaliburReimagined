using Excalibur.Core.Settings;
using Xunit;

namespace Excalibur.Core.Tests;

public class ReleaseFeedTests
{
    private const string Feed = """
        [
          { "tag_name": "v0.2.0", "draft": true,  "prerelease": true, "html_url": "https://x/draft" },
          { "tag_name": "v0.1.3", "draft": false, "prerelease": true, "html_url": "https://x/v0.1.3" },
          { "tag_name": "v0.1.10", "draft": false, "prerelease": true, "html_url": "https://x/v0.1.10" },
          { "tag_name": "nightly", "draft": false, "prerelease": true, "html_url": "https://x/nightly" }
        ]
        """;

    [Fact]
    public void The_highest_published_version_wins_and_drafts_are_skipped()
    {
        var r = ReleaseFeed.Newest(Feed);
        Assert.NotNull(r);
        Assert.Equal(new Version(0, 1, 10), r.Version);
        Assert.Equal("https://x/v0.1.10", r.Url);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "message": "API rate limit exceeded" }""")]
    public void No_release_gives_null(string json) => Assert.Null(ReleaseFeed.Newest(json));

    [Theory]
    [InlineData("0.1.4", "0.1.3.0", true)]
    [InlineData("0.1.3", "0.1.3.0", false)]
    [InlineData("0.1.2", "0.1.3", false)]
    public void Newer_compares_three_parts(string release, string current, bool newer)
        => Assert.Equal(newer, ReleaseFeed.IsNewer(new Release(Version.Parse(release), "v" + release, ""), Version.Parse(current)));
}
