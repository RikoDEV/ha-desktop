using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HaDesktop.Core.Ha;

public enum HaConnectionState { Disconnected, Connecting, Connected, AuthFailed }

/// <summary>
/// Talks to Home Assistant over its native WebSocket API: authenticates,
/// subscribes to state_changed events, and issues service calls.
/// One instance owns one connection; call ConnectAsync to (re)start it.
/// </summary>
public sealed class HaClient : IAsyncDisposable
{
    private readonly HaConnectionSettings _settings;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveLoopCts;
    private Task? _receiveLoopTask;
    private int _nextMessageId = 1;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pendingSystemHealthInitial = new();
    private int _stateChangedSubscriptionId;
    private int _pushNotificationSubscriptionId;
    private string? _pushWebhookId;

    public event Action<HaEntityState>? StateChanged;
    public event Action<HaConnectionState>? ConnectionStateChanged;
    public event Action<HaNotification>? NotificationReceived;

    public HaConnectionState ConnectionState { get; private set; } = HaConnectionState.Disconnected;

    /// <summary>The Home Assistant Core version, from the "ha_version" field HA includes in its auth_ok response — works for every installation type, unlike the Supervisor-only fields in <see cref="GetInstanceInfoAsync"/>.</summary>
    public string? HaVersion { get; private set; }

    public HaClient(HaConnectionSettings settings) => _settings = settings;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(HaConnectionState.Connecting);

        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(_settings.WebSocketUri, ct).ConfigureAwait(false);

        // HA sends {"type":"auth_required"} immediately on connect.
        var hello = await ReceiveJsonAsync(_socket, ct).ConfigureAwait(false);
        if (hello?["type"]?.GetValue<string>() != "auth_required")
            throw new InvalidOperationException("Unexpected HA handshake: " + hello);

        await SendAsync(new JsonObject
        {
            ["type"] = "auth",
            ["access_token"] = _settings.AccessToken,
        }, ct).ConfigureAwait(false);

        var authResult = await ReceiveJsonAsync(_socket, ct).ConfigureAwait(false);
        var authType = authResult?["type"]?.GetValue<string>();
        if (authType != "auth_ok")
        {
            SetState(HaConnectionState.AuthFailed);
            throw new InvalidOperationException("HA auth failed: " + authResult);
        }

        HaVersion = authResult?["ha_version"]?.GetValue<string>();

        _receiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_socket, _receiveLoopCts.Token));

        await SubscribeToStateChangedAsync(_receiveLoopCts.Token).ConfigureAwait(false);
        SetState(HaConnectionState.Connected);
    }

    public async Task<List<HaEntityState>> GetStatesAsync(CancellationToken ct = default)
    {
        var (_, response) = await SendRequestAsync(new JsonObject { ["type"] = "get_states" }, ct).ConfigureAwait(false);
        var result = response["result"]!.AsArray();
        var states = new List<HaEntityState>(result.Count);
        foreach (var node in result)
            states.Add(ParseEntityState(node!.AsObject()));
        return states;
    }

    public async Task CallServiceAsync(string domain, string service, string entityId, JsonObject? extraData = null, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["type"] = "call_service",
            ["domain"] = domain,
            ["service"] = service,
            ["target"] = new JsonObject { ["entity_id"] = entityId },
        };
        if (extraData is not null)
            payload["service_data"] = extraData;

        await SendRequestAsync(payload, ct).ConfigureAwait(false);
    }

    public Task ToggleAsync(string entityId, CancellationToken ct = default) =>
        CallServiceAsync(entityId.Split('.', 2)[0], "toggle", entityId, null, ct);

    /// <summary>
    /// Forecasts aren't part of a weather entity's state/attributes — they come from a
    /// separate call_service round-trip (weather.get_forecasts, return_response: true),
    /// keyed back by entity_id in the response.
    /// </summary>
    public async Task<List<HaForecastEntry>> GetForecastAsync(string entityId, string forecastType = "daily", CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["type"] = "call_service",
            ["domain"] = "weather",
            ["service"] = "get_forecasts",
            ["service_data"] = new JsonObject { ["type"] = forecastType },
            ["target"] = new JsonObject { ["entity_id"] = entityId },
            ["return_response"] = true,
        };

        var (_, response) = await SendRequestAsync(payload, ct).ConfigureAwait(false);
        var forecastArray = response["result"]?["response"]?[entityId]?["forecast"]?.AsArray();
        if (forecastArray is null) return new List<HaForecastEntry>();

        var entries = new List<HaForecastEntry>(forecastArray.Count);
        foreach (var node in forecastArray)
        {
            if (node is not JsonObject obj) continue;
            entries.Add(new HaForecastEntry(
                DateTime: AsString(obj["datetime"]) is { } dt && DateTimeOffset.TryParse(dt, out var parsed) ? parsed : null,
                Condition: AsString(obj["condition"]),
                Temperature: AsDouble(obj["temperature"]),
                TempLow: AsDouble(obj["templow"]),
                Humidity: AsDouble(obj["humidity"]),
                WindSpeed: AsDouble(obj["wind_speed"])));
        }
        return entries;

        static string? AsString(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        static double? AsDouble(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    }

    /// <summary>
    /// Mirrors what Home Assistant's own Settings → About page shows. Installation type comes
    /// from system_health/info (works on every install); Supervisor/OS versions only exist on
    /// Home Assistant OS or Supervised installs, fetched by proxying to the Supervisor's own API
    /// the same way the HA frontend does (a "supervisor/api" WS call, not a raw REST endpoint —
    /// the Supervisor isn't reachable directly from outside its own network).
    /// </summary>
    public async Task<HaInstanceInfo> GetInstanceInfoAsync(CancellationToken ct = default)
    {
        string? installationType = null;
        try
        {
            var initialData = await GetSystemHealthInitialDataAsync(ct).ConfigureAwait(false);
            installationType = initialData?["homeassistant"]?["info"]?["installation_type"] is JsonValue v && v.TryGetValue<string>(out var it) ? it : null;
        }
        catch { /* system_health may not be loaded — leave installation type unknown */ }

        string? supervisorVersion = null;
        string? osVersion = null;
        if (installationType is "Home Assistant OS" or "Home Assistant Supervised")
        {
            try
            {
                var info = await CallSupervisorApiAsync("/info", ct).ConfigureAwait(false);
                supervisorVersion = AsString(info, "supervisor");
                osVersion = AsString(info, "hassos") ?? AsString(info, "operating_system");
            }
            catch { /* best effort */ }

            if (osVersion is null)
            {
                try
                {
                    var osInfo = await CallSupervisorApiAsync("/os/info", ct).ConfigureAwait(false);
                    osVersion = AsString(osInfo, "version");
                }
                catch { /* best effort */ }
            }
        }

        return new HaInstanceInfo(HaVersion, installationType, supervisorVersion, osVersion);

        static string? AsString(JsonObject? obj, string key) =>
            obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>
    /// system_health/info confirms the subscription with a null "result" and streams the actual
    /// data back as an "initial" event (see HandleMessage), followed by "update"/"finish" events
    /// we don't need — unsubscribing right after "initial" stops the server from continuing to
    /// send those.
    /// </summary>
    private async Task<JsonObject?> GetSystemHealthInitialDataAsync(CancellationToken ct)
    {
        if (_socket is null) throw new InvalidOperationException("Not connected.");

        var id = Interlocked.Increment(ref _nextMessageId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSystemHealthInitial[id] = tcs;

        try
        {
            await SendAsync(new JsonObject { ["id"] = id, ["type"] = "system_health/info" }, ct).ConfigureAwait(false);

            using var reg = ct.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingSystemHealthInitial.TryRemove(id, out _);
            try
            {
                await SendAsync(new JsonObject
                {
                    ["id"] = Interlocked.Increment(ref _nextMessageId),
                    ["type"] = "unsubscribe_events",
                    ["subscription_id"] = id,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* best effort cleanup */ }
        }
    }

    private async Task<JsonObject?> CallSupervisorApiAsync(string endpoint, CancellationToken ct)
    {
        var (_, response) = await SendRequestAsync(new JsonObject
        {
            ["type"] = "supervisor/api",
            ["endpoint"] = endpoint,
            ["method"] = "get",
        }, ct).ConfigureAwait(false);
        return response["result"]?.AsObject();
    }

    private static readonly HttpClient CameraHttp = new();

    /// <summary>Fetches one still frame via HA's camera_proxy REST endpoint. Not a live stream — polling this periodically keeps camera tiles lightweight instead of decoding continuous MJPEG/WebRTC.</summary>
    public async Task<byte[]?> GetCameraSnapshotAsync(string entityId, CancellationToken ct = default)
    {
        try
        {
            var uri = new Uri(_settings.RestBaseUri, $"camera_proxy/{entityId}");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AccessToken);

            using var response = await CameraHttp.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null; // best effort — tile/flyout just keeps showing the last good frame
        }
    }

    /// <summary>
    /// Subscribes to Home Assistant's "Local Push" notification channel — the
    /// same WebSocket-based delivery the official Companion Apps use when not
    /// going through FCM/APNs. Requires the device to have registered with
    /// app_data.push_websocket_channel = true (see HaMobileAppClient).
    /// </summary>
    public async Task SubscribeToPushNotificationsAsync(string webhookId, CancellationToken ct = default)
    {
        _pushWebhookId = webhookId;
        var (id, _) = await SendRequestAsync(new JsonObject
        {
            ["type"] = "mobile_app/push_notification_channel",
            ["webhook_id"] = webhookId,
            ["support_confirm"] = true,
        }, ct).ConfigureAwait(false);
        _pushNotificationSubscriptionId = id;
    }

    private async Task SubscribeToStateChangedAsync(CancellationToken ct)
    {
        var (id, _) = await SendRequestAsync(new JsonObject
        {
            ["type"] = "subscribe_events",
            ["event_type"] = "state_changed",
        }, ct).ConfigureAwait(false);
        _stateChangedSubscriptionId = id;
    }

    private async Task<(int Id, JsonObject Result)> SendRequestAsync(JsonObject payload, CancellationToken ct)
    {
        if (_socket is null) throw new InvalidOperationException("Not connected.");

        var id = Interlocked.Increment(ref _nextMessageId);
        payload["id"] = id;

        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        await SendAsync(payload, ct).ConfigureAwait(false);

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        var result = await tcs.Task.ConfigureAwait(false);
        return (id, result.AsObject());
    }

    private async Task SendAsync(JsonNode payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await _socket!.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var json = await ReceiveJsonAsync(socket, ct).ConfigureAwait(false);
                if (json is null) break; // server sent a close frame
                HandleMessage(json);
            }
        }
        catch (OperationCanceledException) { /* deliberate shutdown via DisposeAsync, ct was cancelled */ }
        catch (WebSocketException) { /* handled below */ }

        // Any exit that wasn't a deliberate cancellation (clean server close, dropped
        // connection, HA restart) is an unexpected disconnect callers should react to.
        if (!ct.IsCancellationRequested)
            SetState(HaConnectionState.Disconnected);
    }

    private void HandleMessage(JsonNode json)
    {
        var type = json["type"]?.GetValue<string>();
        switch (type)
        {
            case "result":
                var id = json["id"]?.GetValue<int>();
                if (id is int msgId && _pending.TryRemove(msgId, out var tcs))
                {
                    var success = json["success"]?.GetValue<bool>() ?? false;
                    if (success) tcs.TrySetResult(json);
                    else tcs.TrySetException(new InvalidOperationException("HA request failed: " + json["error"]));
                }
                break;

            case "event":
                var eventId = json["id"]?.GetValue<int>();
                if (eventId == _pushNotificationSubscriptionId)
                {
                    HandlePushNotificationEvent(json["event"]!.AsObject());
                    break;
                }

                // system_health/info is a subscription, not a plain request/response — its
                // "result" message always carries a null payload; the actual data streams back
                // as an "initial" event (then "update"/"finish", which we don't need here).
                if (eventId is int healthId && _pendingSystemHealthInitial.TryGetValue(healthId, out var healthTcs))
                {
                    if (json["event"]?["type"]?.GetValue<string>() == "initial"
                        && json["event"]?["data"] is JsonObject data)
                    {
                        healthTcs.TrySetResult(data);
                    }
                    break;
                }

                var eventData = json["event"]?["data"];
                var newState = eventData?["new_state"];
                if (newState is not null)
                    StateChanged?.Invoke(ParseEntityState(newState.AsObject()));
                break;
        }
    }

    private void HandlePushNotificationEvent(JsonObject eventObj)
    {
        var message = eventObj["message"]?.GetValue<string>();
        if (message is null) return; // not a real notification (e.g. a channel control message)

        var title = eventObj["title"]?.GetValue<string>();
        NotificationReceived?.Invoke(new HaNotification(title, message));

        var confirmId = eventObj["hass_confirm_id"]?.GetValue<string>();
        if (confirmId is not null)
            _ = SendConfirmAsync(confirmId);
    }

    private async Task SendConfirmAsync(string confirmId)
    {
        if (_pushWebhookId is null || _socket is not { State: WebSocketState.Open }) return;

        try
        {
            await SendAsync(new JsonObject
            {
                ["id"] = Interlocked.Increment(ref _nextMessageId),
                ["type"] = "mobile_app/push_notification_confirm",
                ["webhook_id"] = _pushWebhookId,
                ["confirm_id"] = confirmId,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best effort — a missed confirm just means HA may retry/fallback to cloud push for this one
        }
    }

    private static HaEntityState ParseEntityState(JsonObject obj)
    {
        var attributes = new Dictionary<string, object?>();
        if (obj["attributes"] is JsonObject attrObj)
        {
            foreach (var (key, value) in attrObj)
                attributes[key] = ToClrValue(value);
        }

        return new HaEntityState
        {
            EntityId = obj["entity_id"]!.GetValue<string>(),
            State = obj["state"]!.GetValue<string>(),
            Attributes = attributes,
        };
    }

    private static object? ToClrValue(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToString(),
        _ => node.ToJsonString(), // nested object/array: keep as raw JSON text
    };

    private static async Task<JsonNode?> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        stream.Position = 0;
        return JsonNode.Parse(stream);
    }

    private void SetState(HaConnectionState state)
    {
        ConnectionState = state;
        ConnectionStateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        _receiveLoopCts?.Cancel();
        if (_socket is { State: WebSocketState.Open })
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { /* best effort */ }
        }
        _socket?.Dispose();
        if (_receiveLoopTask is not null)
        {
            try { await _receiveLoopTask; } catch { /* already handled */ }
        }
    }
}
