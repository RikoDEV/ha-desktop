using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace HaDesktop.Core.Ha;

/// <summary>
/// Browser-based Home Assistant login (RFC 8252 loopback flow), the same
/// pattern HA's own mobile apps use instead of pasting a long-lived token:
/// spin up a local HTTP listener, send the user's browser to HA's
/// /auth/authorize page, and catch the redirect back with the auth code.
///
/// client_id and redirect_uri are both http://127.0.0.1:{port}/... — HA's
/// IndieAuth client-id verification requires them to share the same host
/// and port, which a loopback address trivially satisfies without needing
/// to host anything.
///
/// Uses PKCE (RFC 7636) on top of that: GetFreeLoopbackPort briefly releases
/// the port before HttpListener rebinds it, leaving a narrow window where
/// another local process could win the race and receive the browser's
/// redirect (code + state) instead of us. PKCE means that stolen code is
/// useless without the code_verifier this process never shares with anyone,
/// since only the token exchange below ever sends it.
/// </summary>
public static class HaOAuthLogin
{
    public static async Task<HaOAuthCredentials> LoginAsync(string baseUrl, CancellationToken ct = default)
    {
        baseUrl = baseUrl.TrimEnd('/');
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Home Assistant URL must start with http:// or https://.");

        var port = GetFreeLoopbackPort();
        var redirectUri = $"http://127.0.0.1:{port}/callback";
        var clientId = $"http://127.0.0.1:{port}/";
        var state = Guid.NewGuid().ToString("N");
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(codeVerifier);

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        try
        {
            var authorizeUrl =
                $"{baseUrl}/auth/authorize?response_type=code" +
                $"&client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&state={state}" +
                $"&code_challenge={codeChallenge}" +
                "&code_challenge_method=S256";

            Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

            var code = await WaitForCallbackAsync(listener, state, ct).ConfigureAwait(false);
            return await ExchangeCodeAsync(baseUrl, clientId, redirectUri, code, codeVerifier, ct).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    private static string GenerateCodeVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string ComputeCodeChallenge(string codeVerifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string> WaitForCallbackAsync(HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var registration = ct.Register(() => listener.Stop());

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Login was cancelled.", ct);
        }
        catch (HttpListenerException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Login was cancelled.", ct);
        }

        var query = context.Request.QueryString;
        var error = query["error"];
        var returnedState = query["state"];
        var code = query["code"];
        var ok = error is null && returnedState == expectedState && code is not null;

        await RespondAsync(context, ok, ct).ConfigureAwait(false);

        if (error is not null)
            throw new InvalidOperationException($"Home Assistant login failed: {error}");
        if (returnedState != expectedState)
            throw new InvalidOperationException("OAuth state mismatch — aborting login for safety.");
        if (code is null)
            throw new InvalidOperationException("Home Assistant did not return an authorization code.");

        return code;
    }

    private static async Task RespondAsync(HttpListenerContext context, bool ok, CancellationToken ct)
    {
        var html = ok
            ? "<html><body style=\"font-family:sans-serif;padding:40px;text-align:center\"><h2>Signed in</h2><p>You can close this window and return to HA Desktop.</p></body></html>"
            : "<html><body style=\"font-family:sans-serif;padding:40px;text-align:center\"><h2>Sign-in failed</h2><p>Close this window and try again from HA Desktop.</p></body></html>";

        var buffer = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer, ct).ConfigureAwait(false);
        context.Response.OutputStream.Close();
    }

    private static async Task<HaOAuthCredentials> ExchangeCodeAsync(string baseUrl, string clientId, string redirectUri, string code, string codeVerifier, CancellationToken ct)
    {
        using var http = new HttpClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier,
        });

        using var response = await http.PostAsync($"{baseUrl}/auth/token", form, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HA token exchange failed ({(int)response.StatusCode}): {body}");

        var json = JsonNode.Parse(body)!.AsObject();
        return new HaOAuthCredentials
        {
            BaseUrl = baseUrl,
            ClientId = clientId,
            AccessToken = json["access_token"]!.GetValue<string>(),
            RefreshToken = json["refresh_token"]!.GetValue<string>(),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(json["expires_in"]!.GetValue<double>()),
        };
    }

    private static int GetFreeLoopbackPort()
    {
        // HttpListener can't bind port 0 directly; ask the OS for a free
        // loopback port via a throwaway socket, then reuse the number.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
