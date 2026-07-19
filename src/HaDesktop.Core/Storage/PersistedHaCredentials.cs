namespace HaDesktop.Core.Storage;

/// <summary>The subset of an OAuth session worth persisting between launches — never the short-lived access token.</summary>
public sealed record PersistedHaCredentials(string BaseUrl, string ClientId, string RefreshToken);
