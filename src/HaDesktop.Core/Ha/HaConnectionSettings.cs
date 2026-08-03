namespace HaDesktop.Core.Ha;

public sealed class HaConnectionSettings
{
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Resolves the access token at call time instead of freezing whatever string happened to be
    /// valid when this object was built. HA access tokens last ~30 minutes and get refreshed in the
    /// background, so anything holding a settings object across that boundary — a camera tile
    /// polling camera_proxy every 10s, the media widget fetching album art, the mobile_app
    /// registration calls — used to keep sending an expired bearer token.
    ///
    /// That matters far more than a failed image fetch: HA's http/ban.py middleware turns *any*
    /// HTTPUnauthorized response into a process_wrong_login strike, and that counter only resets
    /// when HA itself restarts. A poller quietly 401-ing every 10 seconds therefore crosses
    /// login_attempts_threshold and gets the machine's IP banned — which is what kept happening
    /// overnight, roughly a day after each sign-in.
    /// </summary>
    public required Func<string> AccessTokenProvider { get; init; }

    public string AccessToken => AccessTokenProvider();

    public Uri WebSocketUri => new(
        BaseUrl.Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/') + "/api/websocket");

    public Uri RestBaseUri => new(BaseUrl.TrimEnd('/') + "/api/");
}
