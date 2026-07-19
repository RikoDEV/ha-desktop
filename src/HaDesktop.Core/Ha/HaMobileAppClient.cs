using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace HaDesktop.Core.Ha;

/// <summary>
/// Registers this app as a proper Home Assistant "mobile_app" integration —
/// the same protocol the official Companion Apps use — instead of pushing
/// raw, ungrouped sensor.* states via the plain REST API. This gets the app
/// its own Device entry in HA, with all its sensors grouped underneath.
/// </summary>
public sealed class HaMobileAppClient
{
    private readonly HttpClient _http;

    public HaMobileAppClient(HttpClient http) => _http = http;

    public async Task<string> RegisterAsync(HaConnectionSettings settings, string deviceId, string deviceName, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["device_id"] = deviceId,
            ["app_id"] = "ha_desktop",
            ["app_name"] = "HA Desktop",
            ["app_version"] = typeof(HaMobileAppClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            ["device_name"] = deviceName,
            ["manufacturer"] = "HA Desktop",
            ["model"] = "Desktop",
            ["os_name"] = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux",
            ["os_version"] = Environment.OSVersion.VersionString,
            ["supports_encryption"] = false,
            // Opts into HA's "Local Push" delivery — notify.mobile_app_<device> calls are
            // then pushed straight over the WebSocket connection instead of requiring FCM/APNs.
            ["app_data"] = new JsonObject { ["push_websocket_channel"] = true },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.RestBaseUri, "mobile_app/registrations"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);

        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"mobile_app registration failed ({(int)response.StatusCode}): {responseBody}");

        var json = JsonNode.Parse(responseBody)!.AsObject();
        return json["webhook_id"]!.GetValue<string>();
    }

    public Task RegisterSensorAsync(HaConnectionSettings settings, string webhookId, MobileAppSensor sensor, CancellationToken ct = default) =>
        PostWebhookAsync(settings, webhookId, "register_sensor", sensor.ToRegisterPayload(), ct);

    /// <summary>
    /// Updates fields (e.g. device_name) on the device this webhook_id already belongs to. Unlike
    /// POST /api/mobile_app/registrations — which HA's config flow always treats as creating a brand
    /// new device, even when called again with the identical device_id/app_id — this genuinely edits
    /// the existing one in place.
    /// </summary>
    public Task UpdateRegistrationAsync(HaConnectionSettings settings, string webhookId, string deviceName, CancellationToken ct = default) =>
        PostWebhookAsync(settings, webhookId, "update_registration", new JsonObject { ["device_name"] = deviceName }, ct);

    public Task UpdateSensorStatesAsync(HaConnectionSettings settings, string webhookId, IEnumerable<MobileAppSensor> sensors, CancellationToken ct = default)
    {
        var array = new JsonArray();
        foreach (var sensor in sensors)
            array.Add(sensor.ToUpdatePayload());
        return PostWebhookAsync(settings, webhookId, "update_sensor_states", array, ct);
    }

    /// <summary>
    /// Fires an arbitrary HA event as this device — used to report an actionable-notification
    /// button press back as a "mobile_app_notification_action" event, the same protocol the
    /// official companion apps use (see developers.home-assistant.io "Sending data" docs).
    /// </summary>
    public Task FireEventAsync(HaConnectionSettings settings, string webhookId, string eventType, JsonObject eventData, CancellationToken ct = default) =>
        PostWebhookAsync(settings, webhookId, "fire_event", new JsonObject
        {
            ["event_type"] = eventType,
            ["event_data"] = eventData,
        }, ct);

    private async Task PostWebhookAsync(HaConnectionSettings settings, string webhookId, string type, JsonNode data, CancellationToken ct)
    {
        var body = new JsonObject { ["type"] = type, ["data"] = data };

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.RestBaseUri, $"webhook/{webhookId}"))
        {
            // The webhook_id itself is the credential — HA's webhook endpoint doesn't require a bearer token.
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // HA returns 404/410 when the device behind this webhook_id no longer
            // exists (e.g. the user deleted it from Settings > Devices & Services).
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                throw new MobileAppWebhookNotFoundException($"webhook '{webhookId}' no longer exists ({(int)response.StatusCode})");

            throw new InvalidOperationException($"mobile_app webhook '{type}' failed ({(int)response.StatusCode}): {responseBody}");
        }
    }
}
