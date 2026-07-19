namespace HaDesktop.Core.Ha;

public sealed class HaConnectionSettings
{
    public required string BaseUrl { get; init; }
    public required string AccessToken { get; init; }

    public Uri WebSocketUri => new(
        BaseUrl.Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/') + "/api/websocket");

    public Uri RestBaseUri => new(BaseUrl.TrimEnd('/') + "/api/");
}
