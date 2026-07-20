using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Notifications;
using HaDesktop.Core.Sensors;
using HaDesktop.Core.Storage;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

/// <summary>Holds the current HA connection for this session, persisting the OAuth refresh token via the OS credential store.</summary>
public static class AppSettings
{
    private static readonly HttpClient SensorHttp = new();
    private static readonly HaMobileAppClient MobileAppClient = new(SensorHttp);

    private static Timer? _refreshTimer;
    private static Timer? _sensorTimer;
    private static Timer? _registrationHealthTimer;

    public static HaOAuthCredentials? Credentials { get; private set; }
    public static HaClient? Client { get; private set; }
    public static List<TileConfig> SelectedTiles { get; private set; } = new();
    public static SensorPreferences SensorPrefs { get; private set; } = SensorPreferences.Default;
    public static AppearancePreferences Appearance { get; private set; } = AppearancePreferences.Default;
    public static WeatherPreferences WeatherPrefs { get; private set; } = WeatherPreferences.Default;
    public static MediaPlayerPreferences MediaPlayerPrefs { get; private set; } = MediaPlayerPreferences.Default;
    public static MobileAppRegistration? Registration { get; private set; }
    public static bool NotificationsEnabled { get; private set; } = true;
    public static AppLanguage Language => Loc.Instance.Current;

    /// <summary>Most recent notifications received via HA's Local Push channel, newest first. In-memory only, capped at 10.</summary>
    public static List<NotificationHistoryEntry> RecentNotifications { get; } = new();

    /// <summary>Raised whenever a new client becomes connected (or reconnected after a token refresh) or the tile selection/customization changes.</summary>
    public static event Action? ConnectionChanged;

    /// <summary>Raised whenever a notification is received (history updated).</summary>
    public static event Action? NotificationHistoryChanged;

    public static Task SendTestNotificationAsync() =>
        NativeNotifier.Current.ShowAsync(Loc.Instance.Tr("Notification.TestTitle"), Loc.Instance.Tr("Notification.TestBody"), null, Array.Empty<NotificationAction>(), silent: false);

    public static async Task LoadLocalPreferencesAsync()
    {
        var loadedTiles = await TilePreferencesStore.LoadAsync();
        SelectedTiles = TileLayoutCompactor.Defragment(loadedTiles);
        // Persist immediately if defragmenting actually changed anything, so a legacy tiles.json
        // (unpositioned entirely, or with gaps left over from an older version) is normalized
        // once rather than being recomputed — harmlessly, but pointlessly — on every load.
        if (loadedTiles.Count > 0)
            await TilePreferencesStore.SaveAsync(SelectedTiles);
        SensorPrefs = await SensorPreferencesStore.LoadAsync();
        Appearance = await AppearancePreferencesStore.LoadAsync();
        WeatherPrefs = await WeatherPreferencesStore.LoadAsync();
        MediaPlayerPrefs = await MediaPlayerPreferencesStore.LoadAsync();
        Registration = await MobileAppRegistrationStore.LoadAsync();
        NotificationsEnabled = await NotificationPreferencesStore.LoadAsync();

        var languagePrefs = await LanguagePreferencesStore.LoadAsync();
        Loc.Instance.SetLanguage(languagePrefs.Language);
    }

    public static async Task SetLanguageAsync(AppLanguage language)
    {
        Loc.Instance.SetLanguage(language);
        await LanguagePreferencesStore.SaveAsync(new LanguagePreferences(language));
    }

    public static async Task SetNotificationsEnabledAsync(bool enabled)
    {
        NotificationsEnabled = enabled;
        await NotificationPreferencesStore.SaveAsync(enabled);
    }

    public static async Task SetMediaPlayerPreferencesAsync(MediaPlayerPreferences prefs)
    {
        MediaPlayerPrefs = prefs;
        await MediaPlayerPreferencesStore.SaveAsync(prefs);
        ConnectionChanged?.Invoke();
    }

    public static async Task SetWeatherPreferencesAsync(WeatherPreferences prefs)
    {
        WeatherPrefs = prefs;
        await WeatherPreferencesStore.SaveAsync(prefs);
        ConnectionChanged?.Invoke();
    }

    public static async Task SetSelectedTilesAsync(List<TileConfig> tiles)
    {
        // Defragmented here, not just on load, so every mutation path (EntityPickerWindow adding
        // a bare new TileConfig, a resize/group-merge that leaves a hole or an overlap, removing
        // a tile, etc.) always ends up gap-free without each call site needing grid awareness.
        SelectedTiles = TileLayoutCompactor.Defragment(tiles);
        await TilePreferencesStore.SaveAsync(SelectedTiles);
        ConnectionChanged?.Invoke();
    }

    public static async Task UpdateTileAsync(string entityId, string? customLabel, string? customIcon)
    {
        var updated = SelectedTiles
            .Select(t => t.EntityId == entityId ? t with { CustomLabel = customLabel, CustomIcon = customIcon } : t)
            .ToList();
        await SetSelectedTilesAsync(updated);
    }

    public static async Task SetTileSizeAsync(string entityId, TileSize size)
    {
        // Growing a tile (Small -> Wide) can run it off the grid's right edge or straight into a
        // neighbor's cell — SetSelectedTilesAsync's Defragment always fully re-packs the grid
        // gap-free, so it resolves that (and any hole left behind by shrinking) automatically.
        var updated = SelectedTiles.Select(t => t.EntityId == entityId ? t with { Size = size } : t).ToList();
        await SetSelectedTilesAsync(updated);
    }

    /// <summary>
    /// Moves a tile to a new spot in list order — since Defragment always derives every tile's grid
    /// position purely from where it sits in this list, this is the one operation the layout editor's
    /// drag-to-reorder ultimately reduces to (whether dropped in empty space or next to another tile).
    /// </summary>
    public static async Task MoveTileToIndexAsync(string entityId, int index)
    {
        var moved = SelectedTiles.FirstOrDefault(t => t.EntityId == entityId);
        if (moved is null) return;

        var others = SelectedTiles.Where(t => t.EntityId != entityId).ToList();
        others.Insert(Math.Clamp(index, 0, others.Count), moved);
        await SetSelectedTilesAsync(others);
    }

    /// <summary>Merges two small tiles into a new 2x2 Group tile at the target's former position — dragging one small tile onto another.</summary>
    public static async Task CreateGroupAsync(string targetEntityId, string draggedEntityId)
    {
        var target = SelectedTiles.FirstOrDefault(t => t.EntityId == targetEntityId);
        if (target is null) return;

        var group = TileConfig.NewGroup(targetEntityId, draggedEntityId, target.Row, target.Col);
        var updated = SelectedTiles
            .Where(t => t.EntityId != targetEntityId && t.EntityId != draggedEntityId)
            .Append(group)
            .ToList();
        await SetSelectedTilesAsync(updated);
    }

    /// <summary>Adds one more entity into an existing Group's quadrants (up to 4) — dragging a small tile onto a Group with room left.</summary>
    public static async Task AddToGroupAsync(string groupId, string entityId)
    {
        var group = SelectedTiles.FirstOrDefault(t => t.EntityId == groupId);
        if (group?.GroupEntityIds is not { Count: < 4 } members) return;

        var updated = SelectedTiles
            .Where(t => t.EntityId != entityId)
            .Select(t => t.EntityId == groupId ? t with { GroupEntityIds = new List<string>(members) { entityId } } : t)
            .ToList();
        await SetSelectedTilesAsync(updated);
    }

    /// <summary>
    /// Removes one entity from a Group back onto the main grid as its own Small tile, in the group's
    /// former list slot — the layout editor's "drag a tile out of a group" gesture calls this first,
    /// then repositions the freshly-extracted tile whereever it was actually dropped. Dissolves the
    /// Group entirely (converting the sole remaining entity back to a bare Small TileConfig) once
    /// only one member is left, or removes the Group outright if it was the last member.
    /// </summary>
    public static async Task RemoveFromGroupAsync(string groupId, string entityId)
    {
        var group = SelectedTiles.FirstOrDefault(t => t.EntityId == groupId);
        if (group?.GroupEntityIds is null) return;

        var remaining = group.GroupEntityIds.Where(id => id != entityId).ToList();
        var updated = SelectedTiles
            .SelectMany(t =>
            {
                if (t.EntityId != groupId) return new[] { t };
                return remaining.Count switch
                {
                    0 => Array.Empty<TileConfig>(),
                    1 => new[] { new TileConfig(remaining[0]) },
                    _ => new[] { group with { GroupEntityIds = remaining } },
                };
            })
            .Append(new TileConfig(entityId)) // unpositioned — repositioned right after by the caller
            .ToList();

        await SetSelectedTilesAsync(updated);
    }

    public static async Task SetAppearanceAsync(AppearancePreferences appearance)
    {
        Appearance = appearance;
        await AppearancePreferencesStore.SaveAsync(appearance);
        ConnectionChanged?.Invoke();
    }

    public static async Task SetSensorPreferencesAsync(SensorPreferences prefs)
    {
        var deviceRenamed = Registration is not null && prefs.DeviceName != SensorPrefs.DeviceName;
        SensorPrefs = prefs;
        await SensorPreferencesStore.SaveAsync(prefs);

        // update_registration edits the existing device in place. Calling RegisterAsync
        // again here was the previous (wrong) approach — HA's mobile_app config flow has
        // no dedup logic and unconditionally creates a brand-new device on every call to
        // POST /api/mobile_app/registrations, even with an identical device_id/app_id.
        // That was silently spawning a duplicate device in HA on every rename.
        if (deviceRenamed && Credentials is not null)
        {
            try
            {
                await MobileAppClient.UpdateRegistrationAsync(Credentials.ToConnectionSettings(), Registration!.WebhookId, prefs.DeviceName);
            }
            catch (MobileAppWebhookNotFoundException)
            {
                // The device really was deleted — this is the one legitimate case for a fresh registration.
                await ForceReregisterAsync();
            }
            catch
            {
                // best effort — HA keeps showing the old device name until this succeeds
            }
        }

        UpdateSensorTimer();
    }

    /// <summary>Tries to resume a previous session without prompting the browser login again.</summary>
    public static async Task<bool> TryRestoreAsync()
    {
        var saved = await CredentialStore.Current.LoadAsync();
        if (saved is null) return false;

        Credentials = new HaOAuthCredentials
        {
            BaseUrl = saved.BaseUrl,
            ClientId = saved.ClientId,
            RefreshToken = saved.RefreshToken,
            AccessToken = string.Empty,
            ExpiresAtUtc = DateTimeOffset.MinValue, // force an immediate refresh before first use
        };

        try
        {
            await Credentials.RefreshAsync();
        }
        catch
        {
            // The refresh token itself was rejected (revoked, HA reinstalled, etc.) — this is the
            // one case that actually means the saved session is gone for good.
            Credentials = null;
            await CredentialStore.Current.ClearAsync();
            return false;
        }

        try
        {
            await EstablishClientAsync();
            ScheduleRefresh();
            return true;
        }
        catch
        {
            // Refresh succeeded — the session itself is still valid — but connecting failed for
            // some other reason (HA temporarily unreachable, a network blip during an abrupt
            // restart, etc.). Keep the saved credentials so the next retry/relaunch can still use
            // them instead of forcing the user through a full sign-in again.
            return false;
        }
    }

    public static async Task ConnectWithOAuthAsync(HaOAuthCredentials credentials)
    {
        Credentials = credentials;
        await EstablishClientAsync();
        ScheduleRefresh();

        await CredentialStore.Current.SaveAsync(
            new PersistedHaCredentials(credentials.BaseUrl, credentials.ClientId, credentials.RefreshToken));
    }

    public static async Task SignOutAsync()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _sensorTimer?.Dispose();
        _sensorTimer = null;
        _registrationHealthTimer?.Dispose();
        _registrationHealthTimer = null;

        if (Client is not null)
        {
            await Client.DisposeAsync();
            Client = null;
        }

        // Best-effort: revoke the refresh token on HA's side too, so a copy of it sitting in a
        // credential-store backup or an old machine image doesn't stay valid forever after the
        // user believes they've signed out. Local state is cleared regardless of whether this
        // succeeds (HA unreachable, already revoked, etc.).
        if (Credentials is not null)
        {
            try { await Credentials.RevokeAsync(); }
            catch { /* best effort */ }
        }

        Credentials = null;
        Registration = null;
        await CredentialStore.Current.ClearAsync();
        await MobileAppRegistrationStore.ClearAsync();
        ConnectionChanged?.Invoke();
    }

    private static async Task EstablishClientAsync()
    {
        if (Client is not null)
            await Client.DisposeAsync();

        var client = new HaClient(Credentials!.ToConnectionSettings());
        // DisposeAsync (sign-out, or this same method replacing an old client during a
        // scheduled refresh) cancels the receive loop without raising Disconnected, so
        // this only fires for a genuinely unexpected drop — safe to always reconnect on.
        client.ConnectionStateChanged += state =>
        {
            if (state == HaConnectionState.Disconnected)
                _ = ReconnectWithBackoffAsync(client);
        };

        client.NotificationReceived += OnNotificationReceived;
        client.CommandReceived += OnRemoteCommandReceived;

        Client = client;
        await client.ConnectAsync();
        ConnectionChanged?.Invoke();
        UpdateSensorTimer();

        // Registration (and therefore notifications) work independently of sensor
        // sharing — a user may want push notifications without sharing any sensors.
        await EnsureMobileAppRegisteredAsync();
        await SubscribeToPushNotificationsAsync(client);

        _registrationHealthTimer ??= new Timer(_ => _ = VerifyRegistrationAsync(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    private static async Task SubscribeToPushNotificationsAsync(HaClient client)
    {
        if (Registration is null) return;

        try
        {
            await client.SubscribeToPushNotificationsAsync(Registration.WebhookId);
        }
        catch (MobileAppWebhookNotFoundException)
        {
            // The device was deleted from HA's UI since we last registered — the cached
            // webhook_id is dead. Forget it and register fresh, then retry once.
            await ForceReregisterAsync();
            if (Registration is not null)
            {
                try { await client.SubscribeToPushNotificationsAsync(Registration.WebhookId); }
                catch { /* best effort — will retry on next reconnect */ }
            }
        }
        catch
        {
            // best effort — will retry on next reconnect
        }
    }

    /// <summary>
    /// Periodic check independent of sensor sharing (which may be entirely off) — the
    /// only way to notice a registration was deleted in HA if all we otherwise do is
    /// receive pushes, which don't tell us anything when nothing was sent.
    /// </summary>
    private static async Task VerifyRegistrationAsync()
    {
        if (Credentials is null || Registration is null) return;

        try
        {
            await MobileAppClient.UpdateSensorStatesAsync(Credentials.ToConnectionSettings(), Registration.WebhookId, Array.Empty<MobileAppSensor>());
        }
        catch (MobileAppWebhookNotFoundException)
        {
            await ForceReregisterAsync();
            if (Registration is not null && Client is not null)
            {
                try { await Client.SubscribeToPushNotificationsAsync(Registration.WebhookId); }
                catch { /* best effort — will retry next health check */ }
            }
        }
        catch
        {
            // transient network error etc. — not evidence the registration itself is gone
        }
    }

    private static async Task ForceReregisterAsync()
    {
        Registration = null;
        await MobileAppRegistrationStore.ClearAsync();
        await EnsureMobileAppRegisteredAsync();
    }

    private static void OnNotificationReceived(HaNotification notification)
    {
        RecentNotifications.Insert(0, new NotificationHistoryEntry(notification.Title, notification.Message, DateTimeOffset.Now));
        while (RecentNotifications.Count > 10)
            RecentNotifications.RemoveAt(RecentNotifications.Count - 1);
        NotificationHistoryChanged?.Invoke();

        if (!NotificationsEnabled) return;
        _ = ShowAndReportActionAsync(notification);
    }

    /// <summary>
    /// Executes a "command_volume_*" push notification against the local system audio endpoint.
    /// Gated on SensorPrefs.ShareVolume — the same toggle that opts into *reading* volume/mute out
    /// to HA also opts into HA being allowed to *change* it, rather than adding a second toggle for
    /// what's really one "volume integration" decision. To trigger this from HA:
    /// <code>
    /// service: notify.mobile_app_&lt;device_slug&gt;
    /// data:
    ///   message: "command_volume_mute"   # or command_volume_unmute / command_volume_toggle_mute
    /// </code>
    /// or, to set an exact level:
    /// <code>
    /// service: notify.mobile_app_&lt;device_slug&gt;
    /// data:
    ///   message: "command_volume_set"
    ///   data:
    ///     volume_level: 40
    /// </code>
    /// </summary>
    private static void OnRemoteCommandReceived(HaRemoteCommand command)
    {
        if (!SensorPrefs.ShareVolume) return;

        var audio = SystemAudioController.Current;
        switch (command.Command.ToLowerInvariant())
        {
            case "command_volume_mute":
                audio.SetMute(true);
                break;
            case "command_volume_unmute":
                audio.SetMute(false);
                break;
            case "command_volume_toggle_mute":
                audio.SetMute(!(audio.GetMuted() ?? false));
                break;
            case "command_volume_set" when command.VolumeLevel is { } level:
                audio.SetVolumePercent(level);
                break;
        }
    }

    /// <summary>
    /// Shows the notification and, if the user clicked an action button, reports it back to HA
    /// as a "mobile_app_notification_action" event — the same protocol the official companion
    /// apps use. On Windows this is a no-op here (ShowAsync always returns null there); the click
    /// is reported by a relaunch of this exe instead — see Program.cs and WindowsNativeNotifier.
    /// </summary>
    private static async Task ShowAndReportActionAsync(HaNotification notification)
    {
        var actionId = await NativeNotifier.Current.ShowAsync(
            notification.Title, notification.Message, notification.ImageBytes,
            notification.Actions ?? Array.Empty<NotificationAction>(), notification.Silent);

        if (actionId is null || Credentials is null || Registration is null) return;

        try
        {
            await MobileAppClient.FireEventAsync(Credentials.ToConnectionSettings(), Registration.WebhookId,
                "mobile_app_notification_action", new JsonObject { ["action"] = actionId });
        }
        catch
        {
            // best effort — the button click still visually registered for the user even if HA never hears about it
        }
    }

    private static void UpdateSensorTimer()
    {
        if (SensorPrefs.AnyEnabled && Client is not null)
        {
            _sensorTimer ??= new Timer(_ => _ = PushSensorsAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }
        else
        {
            _sensorTimer?.Dispose();
            _sensorTimer = null;
        }
    }

    /// <summary>
    /// Registers this app as a mobile_app device on the connected HA instance if it
    /// hasn't been already (or if the connected instance changed since last time).
    /// One device registration is reused for the app's whole lifetime on that instance.
    /// </summary>
    private static async Task EnsureMobileAppRegisteredAsync()
    {
        if (Credentials is null) return;
        if (Registration is not null && Registration.BaseUrl == Credentials.BaseUrl) return;

        try
        {
            var deviceId = Registration?.DeviceId ?? Guid.NewGuid().ToString("N");
            var webhookId = await MobileAppClient.RegisterAsync(Credentials.ToConnectionSettings(), deviceId, SensorPrefs.DeviceName);

            Registration = new MobileAppRegistration(deviceId, Credentials.BaseUrl, webhookId, new List<string>());
            await MobileAppRegistrationStore.SaveAsync(Registration);
        }
        catch
        {
            // best effort — PushSensorsAsync just skips this tick and retries next time
        }
    }

    private static async Task PushSensorsAsync()
    {
        var client = Client;
        var prefs = SensorPrefs;
        if (client is null || Credentials is null || !prefs.AnyEnabled) return;

        await EnsureMobileAppRegisteredAsync();
        if (Registration is null) return;

        SensorSnapshot snapshot;
        try
        {
            snapshot = await SystemSensorCollector.Current.CollectAsync();
        }
        catch
        {
            return; // best effort, try again next tick
        }

        var sensors = new List<MobileAppSensor>();
        void AddPercent(string key, string name, double? value, string? deviceClass, string icon)
        {
            if (value is { } v) sensors.Add(new MobileAppSensor(key, name, Math.Round(v, 1), icon, deviceClass, "%", "measurement"));
        }

        if (prefs.ShareCpu) AddPercent("cpu", "CPU Usage", snapshot.CpuPercent, null, "mdi:chip");
        if (prefs.ShareMemory) AddPercent("memory", "Memory Usage", snapshot.MemoryPercent, null, "mdi:memory");
        if (prefs.ShareBattery) AddPercent("battery", "Battery", snapshot.BatteryPercent, "battery", "mdi:battery");
        if (prefs.ShareDisk) AddPercent("disk", "Disk Usage", snapshot.DiskPercent, null, "mdi:harddisk");
        if (prefs.ShareGpu) AddPercent("gpu", "GPU Usage", snapshot.GpuPercent, null, "mdi:expansion-card");
        if (prefs.ShareStorage) AddPercent("storage", "Storage Used", snapshot.StoragePercent, null, "mdi:database");

        if (prefs.ShareUptime && snapshot.UptimeHours is { } uptime)
            sensors.Add(new MobileAppSensor("uptime", "Uptime", Math.Round(uptime, 1), "mdi:clock-outline", null, "h", "measurement"));
        if (prefs.ShareNetwork && snapshot.NetworkMbps is { } network)
            sensors.Add(new MobileAppSensor("network", "Network Throughput", Math.Round(network, 2), "mdi:network", "data_rate", "Mbit/s", "measurement"));
        if (prefs.ShareDiskThroughput && snapshot.DiskThroughputMbps is { } diskThroughput)
            sensors.Add(new MobileAppSensor("disk_throughput", "Disk Throughput", Math.Round(diskThroughput, 2), "mdi:harddisk", "data_rate", "Mbit/s", "measurement"));
        if (prefs.ShareActiveWindow && !string.IsNullOrEmpty(snapshot.ActiveWindowTitle))
            sensors.Add(new MobileAppSensor("active_window", "Active Window", snapshot.ActiveWindowTitle, "mdi:application-outline"));
        if (prefs.ShareSessionLock && snapshot.IsSessionLocked is { } isLocked)
            sensors.Add(new MobileAppSensor("session_locked", "Session Locked", isLocked ? "Locked" : "Unlocked", isLocked ? "mdi:lock" : "mdi:lock-open-variant"));
        if (prefs.ShareVolume)
        {
            AddPercent("volume", "System Volume", snapshot.VolumePercent, null, "mdi:volume-high");
            if (snapshot.IsMuted is { } isMuted)
                sensors.Add(new MobileAppSensor("muted", "Muted", isMuted ? "Muted" : "Unmuted", isMuted ? "mdi:volume-mute" : "mdi:volume-high"));
        }
        if (prefs.ShareActiveAudioOutput && !string.IsNullOrEmpty(snapshot.ActiveAudioOutput))
            sensors.Add(new MobileAppSensor("active_audio_output", "Active Audio Output", snapshot.ActiveAudioOutput, "mdi:speaker"));
        if (prefs.ShareActiveAudioInput && !string.IsNullOrEmpty(snapshot.ActiveAudioInput))
            sensors.Add(new MobileAppSensor("active_audio_input", "Active Audio Input", snapshot.ActiveAudioInput, "mdi:microphone"));
        if (prefs.ShareAudioOutputInUse && snapshot.IsAudioOutputInUse is { } isOutputInUse)
            sensors.Add(new MobileAppSensor("audio_output_in_use", "Audio Output In Use", isOutputInUse ? "In Use" : "Idle", isOutputInUse ? "mdi:volume-high" : "mdi:volume-off"));
        if (prefs.ShareAudioInputInUse && snapshot.IsAudioInputInUse is { } isInputInUse)
            sensors.Add(new MobileAppSensor("audio_input_in_use", "Audio Input In Use", isInputInUse ? "In Use" : "Idle", isInputInUse ? "mdi:microphone" : "mdi:microphone-off"));
        if (prefs.ShareActiveCamera && !string.IsNullOrEmpty(snapshot.ActiveCamera))
            sensors.Add(new MobileAppSensor("active_camera", "Active Camera", snapshot.ActiveCamera, "mdi:camera"));
        if (prefs.ShareCameraInUse && snapshot.IsCameraInUse is { } isCameraInUse)
            sensors.Add(new MobileAppSensor("camera_in_use", "Camera In Use", isCameraInUse ? "In Use" : "Idle", isCameraInUse ? "mdi:camera" : "mdi:camera-off"));
        if (prefs.ShareSsid && !string.IsNullOrEmpty(snapshot.Ssid))
            sensors.Add(new MobileAppSensor("ssid", "SSID", snapshot.Ssid, "mdi:wifi"));
        if (prefs.ShareBssid && !string.IsNullOrEmpty(snapshot.Bssid))
            sensors.Add(new MobileAppSensor("bssid", "BSSID", snapshot.Bssid, "mdi:wifi-marker"));
        if (prefs.ShareConnectionType && !string.IsNullOrEmpty(snapshot.ConnectionType))
            sensors.Add(new MobileAppSensor("connection_type", "Connection Type", snapshot.ConnectionType, snapshot.ConnectionType == "Wi-Fi" ? "mdi:wifi" : "mdi:ethernet"));
        if (prefs.ShareDisplayCount && snapshot.DisplayCount is { } displayCount)
            sensors.Add(new MobileAppSensor("displays", "Displays", displayCount, "mdi:monitor-multiple", null, null, "measurement"));
        if (prefs.SharePrimaryDisplay && !string.IsNullOrEmpty(snapshot.PrimaryDisplay))
            sensors.Add(new MobileAppSensor("primary_display", "Primary Display", snapshot.PrimaryDisplay, "mdi:monitor"));

        if (sensors.Count == 0) return;

        var settings = Credentials.ToConnectionSettings();
        var newSensors = sensors.Where(s => !Registration.RegisteredSensorKeys.Contains(s.UniqueId)).ToList();

        foreach (var sensor in newSensors)
        {
            try
            {
                await MobileAppClient.RegisterSensorAsync(settings, Registration.WebhookId, sensor);
                Registration.RegisteredSensorKeys.Add(sensor.UniqueId);
            }
            catch (MobileAppWebhookNotFoundException)
            {
                await ForceReregisterAsync();
                return; // fresh registration has no sensors registered yet — clean slate next tick
            }
            catch
            {
                // best effort — this one just gets retried (as a fresh registration) next tick
            }
        }

        if (newSensors.Count > 0)
            await MobileAppRegistrationStore.SaveAsync(Registration);

        try
        {
            await MobileAppClient.UpdateSensorStatesAsync(settings, Registration.WebhookId, sensors);
        }
        catch (MobileAppWebhookNotFoundException)
        {
            await ForceReregisterAsync();
        }
        catch
        {
            // best effort, try again next tick
        }
    }

    private static async Task ReconnectWithBackoffAsync(HaClient failedClient)
    {
        if (!ReferenceEquals(Client, failedClient) || Credentials is null) return;

        var delay = TimeSpan.FromSeconds(2);
        while (ReferenceEquals(Client, failedClient))
        {
            await Task.Delay(delay);
            if (!ReferenceEquals(Client, failedClient)) return;

            try
            {
                // Refresh first — a network blip that outlasted the access token's
                // lifetime would otherwise fail auth and never recover.
                await Credentials!.RefreshAsync();
                await EstablishClientAsync();
                return;
            }
            catch
            {
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    private static void ScheduleRefresh()
    {
        _refreshTimer?.Dispose();

        // Refresh a couple minutes before the access token actually expires
        // (default HA lifetime is 30 min) so the WS connection never lapses.
        var due = Credentials!.ExpiresAtUtc - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        if (due < TimeSpan.FromSeconds(10))
            due = TimeSpan.FromSeconds(10);

        _refreshTimer = new Timer(_ => _ = RefreshAndReconnectAsync(), null, due, Timeout.InfiniteTimeSpan);
    }

    private static async Task RefreshAndReconnectAsync()
    {
        try
        {
            await Credentials!.RefreshAsync();
            await EstablishClientAsync();
        }
        catch
        {
            // TODO: surface reconnect failure via tray icon state instead of silently retrying.
        }
        finally
        {
            ScheduleRefresh();
        }
    }
}
