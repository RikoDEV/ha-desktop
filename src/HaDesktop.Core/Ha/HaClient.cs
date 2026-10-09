using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HaDesktop.Core.Diagnostics;

namespace HaDesktop.Core.Ha;

public enum HaConnectionState { Disconnected, Connecting, Connected, AuthFailed }

/// <summary>
/// HA rejected the access token during the WebSocket handshake. Distinct from every other connect
/// failure because HA counts a rejected handshake as a wrong login (websocket_api/auth.py calls
/// process_wrong_login), and that counter only resets when HA restarts — so retrying the same
/// token on a timer walks straight into an IP ban. Callers must refresh the token before trying
/// again, and give up rather than repeat a second rejection.
/// </summary>
public sealed class HaAuthFailedException(string message) : Exception(message);

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
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentDictionary<int, StatesRequest> _pendingStates = new();
    private readonly ConcurrentDictionary<string, HaEntityState> _states = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pendingSystemHealthInitial = new();
    private int _stateChangedSubscriptionId;
    private int _pushNotificationSubscriptionId;
    private string? _pushWebhookId;
    private volatile bool _disposed;
    private volatile bool _restUnauthorized;

    public event Action<HaEntityState>? StateChanged;
    public event Action<HaConnectionState>? ConnectionStateChanged;
    public event Action<HaNotification>? NotificationReceived;

    /// <summary>
    /// A "reverse control" push notification whose message starts with "command_" — this app's own
    /// convention (not the official Companion apps' command set, which is mobile-specific) for
    /// letting an HA automation act on this machine instead of just showing a notification. Raised
    /// instead of <see cref="NotificationReceived"/>, never both, for the same incoming message.
    /// </summary>
    public event Action<HaRemoteCommand>? CommandReceived;

    /// <summary>Raised once when an authenticated REST call is rejected with 401 — see <see cref="NoteRestUnauthorized"/>. Further REST calls are suppressed until <see cref="ClearRestUnauthorized"/>.</summary>
    public event Action? RestUnauthorized;

    public HaConnectionState ConnectionState { get; private set; } = HaConnectionState.Disconnected;

    /// <summary>The Home Assistant Core version, from the "ha_version" field HA includes in its auth_ok response — works for every installation type, unlike the Supervisor-only fields in <see cref="GetInstanceInfoAsync"/>.</summary>
    public string? HaVersion { get; private set; }

    /// <summary>
    /// Which entities this client keeps in <see cref="States"/> and raises <see cref="StateChanged"/>
    /// for; null means all of them. Home Assistant pushes every state change in the house, so
    /// anything rejected here is dropped before it's parsed, let alone stored.
    /// </summary>
    public Func<string, bool>? StateFilter { get; set; }

    /// <summary>Last known state of every entity passing <see cref="StateFilter"/> — filled by <see cref="RefreshStatesAsync"/>, then kept current from state_changed events.</summary>
    public IReadOnlyDictionary<string, HaEntityState> States => _states;

    /// <summary>False until the first <see cref="RefreshStatesAsync"/> succeeds on this connection.</summary>
    public bool StatesLoaded { get; private set; }

    public HaClient(HaConnectionSettings settings) => _settings = settings;

    private sealed record StatesRequest(
        TaskCompletionSource<List<HaEntityState>> Completion,
        Func<string, bool>? EntityFilter,
        IReadOnlySet<string>? AttributeNames);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(HaConnectionState.Connecting);

        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(_settings.WebSocketUri, ct).ConfigureAwait(false);

        // HA sends {"type":"auth_required"} immediately on connect.
        var handshakeBuffer = new ReceiveBuffer();
        var hello = await ReceiveNodeAsync(_socket, handshakeBuffer, ct).ConfigureAwait(false);
        if (hello?["type"]?.GetValue<string>() != "auth_required")
            throw new InvalidOperationException("Unexpected HA handshake: " + hello);

        await SendAsync(new JsonObject
        {
            ["type"] = "auth",
            ["access_token"] = _settings.AccessToken,
        }, ct).ConfigureAwait(false);

        var authResult = await ReceiveNodeAsync(_socket, handshakeBuffer, ct).ConfigureAwait(false);
        var authType = authResult?["type"]?.GetValue<string>();
        if (authType != "auth_ok")
        {
            SetState(HaConnectionState.AuthFailed);
            throw new HaAuthFailedException("HA auth failed: " + authResult);
        }

        HaVersion = authResult?["ha_version"]?.GetValue<string>();

        _receiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_socket, _receiveLoopCts.Token));

        await SubscribeToStateChangedAsync(_receiveLoopCts.Token).ConfigureAwait(false);
        SetState(HaConnectionState.Connected);
    }

    /// <summary>
    /// Re-reads every entity passing <see cref="StateFilter"/> into <see cref="States"/>. Needed once
    /// per connection, and again whenever the filter starts admitting entities it used to reject.
    /// </summary>
    public async Task RefreshStatesAsync(CancellationToken ct = default)
    {
        var fresh = await GetStatesAsync(StateFilter, null, ct).ConfigureAwait(false);

        var current = new HashSet<string>(fresh.Count);
        foreach (var state in fresh)
        {
            _states[state.EntityId] = state;
            current.Add(state.EntityId);
        }

        foreach (var entityId in _states.Keys)
            if (!current.Contains(entityId)) _states.TryRemove(entityId, out _);

        StatesLoaded = true;
    }

    /// <summary>
    /// A one-off listing that bypasses <see cref="States"/> — for pickers that need entities the app
    /// doesn't otherwise track. Pass <paramref name="attributeNames"/> to keep only the attributes
    /// the caller will actually read.
    /// </summary>
    public async Task<List<HaEntityState>> GetStatesAsync(Func<string, bool>? entityFilter = null, IReadOnlySet<string>? attributeNames = null, CancellationToken ct = default)
    {
        if (_socket is null) throw new InvalidOperationException("Not connected.");

        var id = Interlocked.Increment(ref _nextMessageId);
        var tcs = new TaskCompletionSource<List<HaEntityState>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingStates[id] = new StatesRequest(tcs, entityFilter, attributeNames);

        await SendAsync(new JsonObject { ["id"] = id, ["type"] = "get_states" }, ct).ConfigureAwait(false);

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        return await tcs.Task.ConfigureAwait(false);
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

        var (_, result) = await SendRequestAsync(payload, ct).ConfigureAwait(false);
        var forecastArray = result?["response"]?[entityId]?["forecast"]?.AsArray();
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
        catch (Exception ex) { Log.Swallowed(ex); /* system_health may not be loaded — leave installation type unknown */ }

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
            catch (Exception ex) { Log.Swallowed(ex); /* best effort */ }

            if (osVersion is null)
            {
                try
                {
                    var osInfo = await CallSupervisorApiAsync("/os/info", ct).ConfigureAwait(false);
                    osVersion = AsString(osInfo, "version");
                }
                catch (Exception ex) { Log.Swallowed(ex); /* best effort */ }
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
                    ["subscription"] = id,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Swallowed(ex); /* best effort cleanup */ }
        }
    }

    private async Task<JsonObject?> CallSupervisorApiAsync(string endpoint, CancellationToken ct)
    {
        var (_, result) = await SendRequestAsync(new JsonObject
        {
            ["type"] = "supervisor/api",
            ["endpoint"] = endpoint,
            ["method"] = "get",
        }, ct).ConfigureAwait(false);
        return result as JsonObject;
    }

    /// <summary>Fetches one still frame via HA's camera_proxy REST endpoint. Not a live stream — polling this periodically keeps camera tiles lightweight instead of decoding continuous MJPEG/WebRTC.</summary>
    public async Task<byte[]?> GetCameraSnapshotAsync(string entityId, CancellationToken ct = default)
    {
        if (_disposed || _restUnauthorized) return null;

        try
        {
            var uri = new Uri(_settings.RestBaseUri, $"camera_proxy/{entityId}");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AccessToken);

            using var response = await HaHttp.Client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) NoteRestUnauthorized();
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
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

    /// <summary>Sends a request and returns its id plus the "result" payload of HA's response (null for commands that return nothing).</summary>
    private async Task<(int Id, JsonNode? Result)> SendRequestAsync(JsonObject payload, CancellationToken ct)
    {
        if (_socket is null) throw new InvalidOperationException("Not connected.");

        var id = Interlocked.Increment(ref _nextMessageId);
        payload["id"] = id;

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        await SendAsync(payload, ct).ConfigureAwait(false);

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        return (id, await tcs.Task.ConfigureAwait(false));
    }

    private async Task SendAsync(JsonNode payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await _socket!.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new ReceiveBuffer();
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                if (!await ReceiveMessageAsync(socket, buffer, ct).ConfigureAwait(false))
                    break; // server sent a close frame

                // A single unexpected message shape (an assumption about a field's type that
                // doesn't hold for some payload) must never take down the whole receive loop —
                // that silently stops all future messages (state updates, pushes, everything)
                // without ever disconnecting, which is exactly what an uncaught exception here
                // used to do.
                try { HandleMessage(buffer.AsSequence()); }
                catch (Exception ex) { Log.Swallowed(ex); /* best effort — skip this one message, keep the connection alive */ }
            }
        }
        catch (OperationCanceledException) { /* deliberate shutdown via DisposeAsync, ct was cancelled */ }
        catch (WebSocketException) { /* handled below */ }

        // Any exit that wasn't a deliberate cancellation (clean server close, dropped
        // connection, HA restart) is an unexpected disconnect callers should react to.
        if (!ct.IsCancellationRequested)
            SetState(HaConnectionState.Disconnected);
    }

    private enum MessageKind { Other, Result, Event }

    private void HandleMessage(ReadOnlySequence<byte> message)
    {
        // Two passes: the first only finds "id" and "type" (skipping over everything else without
        // allocating), so the second can read the payload knowing what it is — HA happens to send
        // those two first, but nothing in the protocol promises that order.
        var (id, kind) = ReadEnvelope(message);
        if (kind == MessageKind.Other) return;

        var reader = new Utf8JsonReader(message);
        reader.Read();

        if (kind == MessageKind.Result) HandleResult(id, ref reader);
        else HandleEvent(id, ref reader);
    }

    private static (int? Id, MessageKind Kind) ReadEnvelope(ReadOnlySequence<byte> message)
    {
        var reader = new Utf8JsonReader(message);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return default;

        int? id = null;
        MessageKind? kind = null;
        while ((id is null || kind is null) && reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("id"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value)) id = value;
            }
            else if (reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                kind = reader.TokenType != JsonTokenType.String ? MessageKind.Other
                    : reader.ValueTextEquals("result"u8) ? MessageKind.Result
                    : reader.ValueTextEquals("event"u8) ? MessageKind.Event
                    : MessageKind.Other;
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return (id, kind ?? MessageKind.Other);
    }

    private void HandleResult(int? id, ref Utf8JsonReader reader)
    {
        if (id is not int messageId) return;

        var isStatesRequest = _pendingStates.TryRemove(messageId, out var statesRequest);
        TaskCompletionSource<JsonNode?>? tcs = null;
        if (!isStatesRequest && !_pending.TryRemove(messageId, out tcs)) return;

        try
        {
            var probe = reader;
            var success = HaStateParser.TryMoveToProperty(ref probe, "success"u8) && probe.TokenType == JsonTokenType.True;
            if (!success)
            {
                probe = reader;
                var error = HaStateParser.TryMoveToProperty(ref probe, "error"u8) ? JsonNode.Parse(ref probe) : null;
                throw new InvalidOperationException("HA request failed: " + error);
            }

            var hasResult = HaStateParser.TryMoveToProperty(ref reader, "result"u8);
            if (isStatesRequest)
            {
                statesRequest!.Completion.TrySetResult(hasResult
                    ? HaStateParser.ReadStates(ref reader, statesRequest.EntityFilter, statesRequest.AttributeNames)
                    : new List<HaEntityState>());
            }
            else
            {
                tcs!.TrySetResult(hasResult ? JsonNode.Parse(ref reader) : null);
            }
        }
        catch (Exception ex)
        {
            // The request is already out of the pending table, so it has to be completed here —
            // leaving it would hang its caller forever.
            if (isStatesRequest) statesRequest!.Completion.TrySetException(ex);
            else tcs!.TrySetException(ex);
        }
    }

    private void HandleEvent(int? id, ref Utf8JsonReader reader)
    {
        if (!HaStateParser.TryMoveToProperty(ref reader, "event"u8)) return;

        if (id is int eventId)
        {
            if (eventId == _pushNotificationSubscriptionId)
            {
                if (JsonNode.Parse(ref reader) is JsonObject pushEvent)
                    HandlePushNotificationEvent(pushEvent);
                return;
            }

            // system_health/info is a subscription, not a plain request/response — its
            // "result" message always carries a null payload; the actual data streams back
            // as an "initial" event (then "update"/"finish", which we don't need here).
            if (_pendingSystemHealthInitial.TryGetValue(eventId, out var healthTcs))
            {
                var healthEvent = JsonNode.Parse(ref reader);
                if (healthEvent?["type"]?.GetValue<string>() == "initial" && healthEvent["data"] is JsonObject data)
                    healthTcs.TrySetResult(data);
                return;
            }
        }

        HandleStateChangedEvent(ref reader);
    }

    /// <summary>Reader is on the state_changed event object: {"event_type": ..., "data": {"entity_id", "old_state", "new_state"}}.</summary>
    private void HandleStateChangedEvent(ref Utf8JsonReader reader)
    {
        if (!HaStateParser.TryMoveToProperty(ref reader, "data"u8) || reader.TokenType != JsonTokenType.StartObject) return;

        if (HaStateParser.PeekEntityId(reader) is not { } entityId) return;
        if (StateFilter is { } filter && !filter(entityId)) return;

        if (!HaStateParser.TryMoveToProperty(ref reader, "new_state"u8)) return;
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            _states.TryRemove(entityId, out _); // a null new_state means the entity was removed from HA
            return;
        }

        if (HaStateParser.ReadState(ref reader) is not { } state) return;

        _states[state.EntityId] = state;
        StateChanged?.Invoke(state);
    }

    private void HandlePushNotificationEvent(JsonObject eventObj)
    {
        var message = AsString(eventObj["message"]);
        if (message is null) return; // not a real notification (e.g. a channel control message)

        var data = eventObj["data"] as JsonObject;

        // "command_*" is this app's own convention for a headless, HA -> app instruction (mute,
        // set volume, ...) piggybacked on the same notify.mobile_app_<device> channel used for
        // real notifications — recognized here, before anything gets shown to the user.
        if (message.StartsWith("command_", StringComparison.OrdinalIgnoreCase))
        {
            var volumeLevel = data?["volume_level"] is JsonValue volumeLevelNode && volumeLevelNode.TryGetValue<double>(out var vl) ? vl : (double?)null;
            CommandReceived?.Invoke(new HaRemoteCommand(message, volumeLevel));

            var commandConfirmId = AsString(eventObj["hass_confirm_id"]);
            if (commandConfirmId is not null)
                _ = SendConfirmAsync(commandConfirmId);
            return;
        }

        var title = AsString(eventObj["title"]);

        var actions = data?["actions"]?.AsArray()
            .OfType<JsonObject>()
            .Select(a => (Action: AsString(a["action"]), Title: AsString(a["title"]), Uri: AsString(a["uri"])))
            .Where(a => a.Action is not null && a.Title is not null)
            .Select(a => new NotificationAction(a.Action!, a.Title!, a.Uri))
            .ToList();

        // "data.push.sound" is the iOS/macOS field for this; there's no Android/desktop equivalent
        // field name, so treating "none" there as "silent" is the closest cross-platform mapping.
        // The field can be a plain string ("none") or a richer object (e.g. critical alerts:
        // {"name": "default", "critical": 1, "volume": 1}) — read the name out of either shape
        // rather than assuming a string, which would throw and silently kill the receive loop
        // for every message after it (this exact payload shape did exactly that before this fix).
        var soundNode = data?["push"]?["sound"];
        var soundName = soundNode is JsonObject soundObj ? AsString(soundObj["name"]) : AsString(soundNode);
        var silent = soundName == "none";

        // data.attachment.url is the newer/richer field (supports content-type override,
        // lazy-loading, etc.) and takes precedence over the older data.image per HA's own docs.
        var imageUrl = AsString(data?["attachment"]?["url"]) ?? AsString(data?["image"]);
        _ = HandleNotificationAsync(title, message, imageUrl, actions, silent);

        var confirmId = AsString(eventObj["hass_confirm_id"]);
        if (confirmId is not null)
            _ = SendConfirmAsync(confirmId);
    }

    private static string? AsString(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private async Task HandleNotificationAsync(string? title, string message, string? imageUrl, List<NotificationAction>? actions, bool silent)
    {
        var imageBytes = imageUrl is null ? null : await DownloadImageAsync(imageUrl).ConfigureAwait(false);
        NotificationReceived?.Invoke(new HaNotification(title, message, imageBytes, actions, silent));
    }

    /// <summary>
    /// Fetches an image HA referred to by URL (a notification attachment, a media player's album
    /// art). An absolute URL is fetched as-is; a relative one (e.g. "/local/icon.png") is resolved
    /// against the HA instance and sent with the same bearer token as every other API call. Null on
    /// any failure.
    /// </summary>
    public async Task<byte[]?> DownloadImageAsync(string rawUrl, CancellationToken ct = default)
    {
        if (_disposed) return null;

        try
        {
            Uri uri;
            var needsAuth = false;
            if (rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                uri = new Uri(rawUrl);
            }
            else
            {
                uri = new Uri(new Uri(_settings.BaseUrl.TrimEnd('/') + "/"), rawUrl.TrimStart('/'));
                needsAuth = true;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (needsAuth)
            {
                if (_restUnauthorized) return null;
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AccessToken);
            }

            using var response = await HaHttp.Client.SendAsync(request, ct).ConfigureAwait(false);
            if (needsAuth && response.StatusCode == HttpStatusCode.Unauthorized) NoteRestUnauthorized();
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false) : null;
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            return null; // best effort — show the notification without the image rather than not at all
        }
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
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            // best effort — a missed confirm just means HA may retry/fallback to cloud push for this one
        }
    }

    /// <summary>Receives one whole message into <paramref name="buffer"/>; false if the server sent a close frame instead.</summary>
    private static async Task<bool> ReceiveMessageAsync(ClientWebSocket socket, ReceiveBuffer buffer, CancellationToken ct)
    {
        buffer.Reset();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.GetMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return false;
            buffer.Advance(result.Count);
        } while (!result.EndOfMessage);

        return true;
    }

    /// <summary>Handshake-only: the two small messages exchanged before the receive loop starts.</summary>
    private static async Task<JsonNode?> ReceiveNodeAsync(ClientWebSocket socket, ReceiveBuffer buffer, CancellationToken ct)
    {
        if (!await ReceiveMessageAsync(socket, buffer, ct).ConfigureAwait(false)) return null;
        return ParseNode(buffer.AsSequence());
    }

    private static JsonNode? ParseNode(ReadOnlySequence<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        return JsonNode.Parse(ref reader);
    }

    private void SetState(HaConnectionState state)
    {
        ConnectionState = state;
        ConnectionStateChanged?.Invoke(state);
    }

    /// <summary>
    /// Latches the first 401 from an authenticated REST call so a poller (camera tiles tick every
    /// 10s, the detail flyout every 2s) can't turn one rejected token into an unbounded stream of
    /// them — each 401 is a strike toward HA's IP ban, and that counter never decays on its own.
    /// The owner refreshes the token and calls <see cref="ClearRestUnauthorized"/> to resume.
    /// </summary>
    private void NoteRestUnauthorized()
    {
        if (_restUnauthorized) return;
        _restUnauthorized = true;
        RestUnauthorized?.Invoke();
    }

    public void ClearRestUnauthorized() => _restUnauthorized = false;

    public async ValueTask DisposeAsync()
    {
        // Reflected in ConnectionState — but deliberately without raising ConnectionStateChanged,
        // which is the caller's signal for an *unexpected* drop worth reconnecting after. Anything
        // still holding this client (a camera tile that outlived a reconnect, say) checks
        // ConnectionState before making REST calls, and used to see a stale "Connected" here and
        // keep polling with this client's dead token.
        _disposed = true;
        ConnectionState = HaConnectionState.Disconnected;

        _receiveLoopCts?.Cancel();
        if (_socket is { State: WebSocketState.Open })
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch (Exception ex) { Log.Swallowed(ex); /* best effort */ }
        }
        _socket?.Dispose();
        if (_receiveLoopTask is not null)
        {
            try { await _receiveLoopTask; } catch (Exception ex) { Log.Swallowed(ex); /* already handled */ }
        }
    }
}
