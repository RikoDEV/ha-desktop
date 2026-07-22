using System.Text.Json.Nodes;

namespace HaDesktop.Core.Updates;

public enum AppUpdateCheckStatus { UpToDate, UpdateAvailable, Failed }

/// <summary>LatestVersion/ReleaseUrl are only populated when Status is UpdateAvailable.</summary>
public sealed record AppUpdateCheckResult(AppUpdateCheckStatus Status, string? LatestVersion = null, string? ReleaseUrl = null)
{
    public static readonly AppUpdateCheckResult Failed = new(AppUpdateCheckStatus.Failed);
    public static readonly AppUpdateCheckResult UpToDate = new(AppUpdateCheckStatus.UpToDate);
}

/// <summary>Checks this project's GitHub Releases for a version newer than the one currently running.</summary>
public static class GitHubUpdateChecker
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/RikoDEV/ha-desktop/releases/latest";
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub's REST API rejects anonymous requests that have no User-Agent header.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HaDesktop-Tray-UpdateChecker");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<AppUpdateCheckResult> CheckAsync(Version currentVersion)
    {
        try
        {
            var json = await Http.GetStringAsync(ReleasesApiUrl);
            var release = JsonNode.Parse(json);
            var tag = release?["tag_name"]?.GetValue<string>();
            var url = release?["html_url"]?.GetValue<string>();
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(url))
                return AppUpdateCheckResult.Failed;

            var versionText = tag.TrimStart('v', 'V');
            if (!Version.TryParse(versionText, out var latest))
                return AppUpdateCheckResult.Failed;

            var isNewer = Normalize(latest).CompareTo(Normalize(currentVersion)) > 0;
            return isNewer
                ? new AppUpdateCheckResult(AppUpdateCheckStatus.UpdateAvailable, versionText, url)
                : AppUpdateCheckResult.UpToDate;
        }
        catch
        {
            // Offline, rate-limited, GitHub down, malformed response, etc. — surfaced to the
            // caller as "couldn't check", never silently reported as a false "you're up to date".
            return AppUpdateCheckResult.Failed;
        }
    }

    // Comparisons only look at Major.Minor.Build — an unspecified component parses to -1, which
    // would otherwise sort as "older" than the explicit 0 the assembly version always carries.
    private static Version Normalize(Version v) => new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
