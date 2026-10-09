namespace HaDesktop.Core.Ha;

/// <summary>The one HttpClient (and so the one connection pool) every REST call to Home Assistant shares.</summary>
public static class HaHttp
{
    public static HttpClient Client { get; } = new();
}
