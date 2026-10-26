using System.Text.Json;

namespace Excalibur.Core.Settings;

/// <summary>A published version of the app on GitHub.</summary>
public sealed record Release(Version Version, string Tag, string Url);

/// <summary>
/// Reads GitHub's release list (<c>GET /repos/{owner}/{repo}/releases</c>). Every release so far is a pre-release, which
/// <c>/releases/latest</c> leaves out, so the list is read and the highest version that is not a draft wins.
/// </summary>
public static class ReleaseFeed
{
    public const string ApiUrl = "https://api.github.com/repos/soldsawn/ExcaliburReimagined/releases?per_page=20";

    /// <summary>The newest release in the list; null when it holds none with a version tag (or is not a release list).</summary>
    public static Release? Newest(string json)
    {
        Release? best = null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (!r.TryGetProperty("tag_name", out var tagEl) || tagEl.GetString() is not { } tag) continue;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) continue;
            string url = r.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            if (best is null || version > best.Version) best = new Release(version, tag, url);
        }
        return best;
    }

    /// <summary>True when <paramref name="release"/> is newer than the running <paramref name="current"/> (major.minor.build).</summary>
    public static bool IsNewer(Release release, Version current) =>
        Normalize(release.Version) > Normalize(current);

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
