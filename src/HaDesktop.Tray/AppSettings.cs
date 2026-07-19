using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
        NativeNotifier.Current.ShowAsync(Loc.Instance.Tr("Notification.TestTitle"), Loc.Instance.Tr("Notification.TestBody"));

    public static async Task LoadLocalPreferencesAsync()
    {
        SelectedTiles = await TilePreferencesStore.LoadAsync();
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
        SelectedTiles = tiles;
        await TilePreferencesStore.SaveAsync(tiles);
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
        var updated = SelectedTiles.Select(t => t.EntityId == entityId ? t with { Size = size } : t).ToList();
        await SetSelectedTilesAsync(updated);
    }

    public static async Task MoveTileAsync(string entityId, int direction)
    {
        var index = SelectedTiles.FindIndex(t => t.EntityId == entityId);
        var newIndex = index + direction;
        if (index < 0 || newIndex < 0 || newIndex >= SelectedTiles.Count) return;

        var updated = new List<TileConfig>(SelectedTiles);
        (updated[index], updated[newIndex]) = (updated[newIndex], updated[index]);
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
            await EstablishClientAsync();
            ScheduleRefresh();
            return true;
        }
        catch
        {
            // Saved refresh token no longer valid (revoked, HA reinstalled, etc.) — fall back to login.
            Credentials = null;
            await CredentialStore.Current.ClearAsync();
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
        _ = NativeNotifier.Current.ShowAsync(notification.Title, notification.Message);
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

        if (prefs.ShareUptime && snapshot.UptimeHours is { } uptime)
            sensors.Add(new MobileAppSensor("uptime", "Uptime", Math.Round(uptime, 1), "mdi:clock-outline", null, "h", "measurement"));
        if (prefs.ShareNetwork && snapshot.NetworkMbps is { } network)
            sensors.Add(new MobileAppSensor("network", "Network Throughput", Math.Round(network, 2), "mdi:network", "data_rate", "Mbit/s", "measurement"));
        if (prefs.ShareActiveWindow && !string.IsNullOrEmpty(snapshot.ActiveWindowTitle))
            sensors.Add(new MobileAppSensor("active_window", "Active Window", snapshot.ActiveWindowTitle, "mdi:application-outline"));

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
