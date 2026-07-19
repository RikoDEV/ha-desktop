using System.Text.Json;

namespace HaDesktop.Core.Storage;

/// <summary>
/// Splits the mobile_app registration across two stores: device metadata (device_id, which HA
/// instance it belongs to, and which sensors are already registered) in a plain JSON file — none
/// of that is secret — and the webhook_id itself in the OS secret store. The webhook_id is the
/// real credential here: Home Assistant's mobile_app webhook accepts it with no additional bearer
/// token, and some HA versions honor webhook message types well beyond sensor updates (e.g.
/// call_service), so it gets the same protection as the OAuth refresh token rather than sitting
/// in a plain file.
/// </summary>
public static class MobileAppRegistrationStore
{
    private const string WebhookSecretKey = "mobile-app-webhook";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaDesktop", "mobile-app.json");

    private sealed record PersistedMetadata(string DeviceId, string BaseUrl, List<string> RegisteredSensorKeys);

    public static async Task SaveAsync(MobileAppRegistration registration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var metadata = new PersistedMetadata(registration.DeviceId, registration.BaseUrl, registration.RegisteredSensorKeys);
        await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(metadata));
        await SecretStore.Current.SaveAsync(WebhookSecretKey, registration.WebhookId);
    }

    public static async Task<MobileAppRegistration?> LoadAsync()
    {
        if (!File.Exists(FilePath))
            return null;

        PersistedMetadata? metadata;
        try
        {
            var json = await File.ReadAllTextAsync(FilePath);
            metadata = JsonSerializer.Deserialize<PersistedMetadata>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (metadata is null) return null;

        var webhookId = await SecretStore.Current.LoadAsync(WebhookSecretKey);
        if (webhookId is null) return null; // desynced from the secret store — treat as unregistered rather than reuse a half-known registration

        return new MobileAppRegistration(metadata.DeviceId, metadata.BaseUrl, webhookId, metadata.RegisteredSensorKeys);
    }

    public static async Task ClearAsync()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
        await SecretStore.Current.ClearAsync(WebhookSecretKey);
    }
}
