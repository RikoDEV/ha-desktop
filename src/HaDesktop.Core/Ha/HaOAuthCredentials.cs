using System.Net.Http;
using System.Text.Json.Nodes;

namespace HaDesktop.Core.Ha;

/// <summary>
/// OAuth2 tokens obtained via the browser-based Home Assistant login flow
/// (see <see cref="HaOAuthLogin"/>). Access tokens expire (default 30 min);
/// call <see cref="RefreshAsync"/> before they do to keep the session alive.
/// </summary>
public sealed class HaOAuthCredentials
{
    public required string BaseUrl { get; init; }
    public required string ClientId { get; init; }
    public required string AccessToken { get; set; }
    public required string RefreshToken { get; set; }
    public required DateTimeOffset ExpiresAtUtc { get; set; }

    public HaConnectionSettings ToConnectionSettings() => new() { BaseUrl = BaseUrl, AccessToken = AccessToken };

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = RefreshToken,
            ["client_id"] = ClientId,
        });

        using var response = await http.PostAsync($"{BaseUrl}/auth/token", form, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HA token refresh failed ({(int)response.StatusCode}): {body}");

        var json = JsonNode.Parse(body)!.AsObject();
        AccessToken = json["access_token"]!.GetValue<string>();
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(json["expires_in"]!.GetValue<double>());
        // HA's refresh response does not include a new refresh_token — the original stays valid.
    }

    /// <summary>
    /// Invalidates the refresh token on the HA side, so "Sign Out" actually revokes the session
    /// instead of just forgetting it locally — without this, a copy of the refresh token (e.g.
    /// from a stolen backup of the local credential store) would stay valid indefinitely.
    /// Best-effort: sign-out should still clear local state even if HA is unreachable.
    /// </summary>
    public async Task RevokeAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "revoke",
            ["token"] = RefreshToken,
        });

        using var response = await http.PostAsync($"{BaseUrl}/auth/token", form, ct).ConfigureAwait(false);
        // HA returns 200 with an empty body on success; not treated as fatal either way — see summary above.
    }
}
