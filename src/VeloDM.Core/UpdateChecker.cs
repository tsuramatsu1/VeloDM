using System.Text.Json;

namespace VeloDM.Core;

public sealed record UpdateInfo(Version Version, string PageUrl, string? InstallerUrl, string? Notes);

/// <summary>
/// Looks for a newer VeloDM. The feed is either a GitHub repository address (its latest
/// release is used) or the address of an update.json file:
/// <c>{ "version": "1.4.0", "url": "https://…/VeloDM-1.4.0-x64.msi", "page": "https://…", "notes": "…" }</c>
/// </summary>
public static class UpdateChecker
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Returns the newer version, or null when <paramref name="current"/> is up to date.</summary>
    public static async Task<UpdateInfo?> CheckAsync(string feedUrl, Version current, CancellationToken ct)
    {
        if (!Uri.TryCreate(feedUrl?.Trim(), UriKind.Absolute, out var feed) || feed.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The update address must be an https:// address.");

        bool gitHub = TryGetGitHubRepository(feed, out var owner, out var repository);
        var address = gitHub ? $"https://api.github.com/repos/{owner}/{repository}/releases/latest" : feed.AbsoluteUri;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("User-Agent", "VeloDM-UpdateCheck");
        request.Headers.TryAddWithoutValidation("Accept", gitHub ? "application/vnd.github+json" : "application/json");
        using var response = await HttpEngine.Client.SendAsync(request, cts.Token).ConfigureAwait(false);
        if (gitHub && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null; // no published release yet
        HttpEngine.EnsureSuccess(response);
        var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

        var latest = gitHub ? ParseGitHubRelease(json) : ParseFeed(json, feed);
        return latest is not null && latest.Version > Normalize(current) ? latest : null;
    }

    public static bool TryGetGitHubRepository(Uri feed, out string owner, out string repository)
    {
        owner = repository = "";
        if (!feed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = feed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        owner = parts[0];
        repository = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return true;
    }

    public static UpdateInfo? ParseGitHubRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!TryParseVersion(Text(root, "tag_name"), out var version)) return null;

        string? installer = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var url = Text(asset, "browser_download_url");
                if (IsHttps(url) && url!.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                {
                    installer = url;
                    break;
                }
            }
        }
        var page = Text(root, "html_url");
        return IsHttps(page) ? new UpdateInfo(version, page!, installer, Text(root, "body")) : null;
    }

    public static UpdateInfo? ParseFeed(string json, Uri feed)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!TryParseVersion(Text(root, "version"), out var version)) return null;
        var installer = Text(root, "url");
        var page = Text(root, "page");
        return new UpdateInfo(version,
            IsHttps(page) ? page! : IsHttps(installer) ? installer! : feed.AbsoluteUri,
            IsHttps(installer) ? installer : null,
            Text(root, "notes"));
    }

    /// <summary>Accepts "1.4", "v1.4.0" and "1.4.0-beta" style tags.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var digits = text.Trim().TrimStart('v', 'V');
        int end = 0;
        while (end < digits.Length && (char.IsDigit(digits[end]) || digits[end] == '.')) end++;
        digits = digits[..end].Trim('.');
        if (!digits.Contains('.')) digits += ".0";
        if (!Version.TryParse(digits, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    // 1.4 and 1.4.0.0 must compare as equal.
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
}
