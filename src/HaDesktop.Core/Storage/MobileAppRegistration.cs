namespace HaDesktop.Core.Storage;

/// <summary>Which HA instance this app is registered as a mobile_app device against, and which sensors it has already told HA about.</summary>
public sealed record MobileAppRegistration(string DeviceId, string BaseUrl, string WebhookId, List<string> RegisteredSensorKeys);
