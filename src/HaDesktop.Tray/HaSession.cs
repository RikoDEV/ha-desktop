using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HaDesktop.Core.Diagnostics;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>
/// The app's one Home Assistant session: the OAuth credentials (refresh token persisted via the OS
/// credential store), the live WebSocket client and its reconnect/token-refresh lifecycle, and this
/// machine's mobile_app device registration.
/// </summary>
public static class HaSession
{
    internal static readonly HaMobileAppClient MobileAppClient = new(HaHttp.Client);

    private static Timer? _refreshTimer;
    private static Timer? _registrationHealthTimer;

    // Guards EnsureMobileAppRegisteredAsync end-to-end. Without this, two callers racing while
    // Registration is null (e.g. EstablishClientAsync's own call landing at the same moment as the
    // sensor timer's immediate first tick, or the sensor timer firing again mid-flight of a
    // ForceReregisterAsync retry) would both see no registration and both POST a brand-new device
    // to HA — the previous device is never deleted, so it just sits there duplicated.
    private static readonly SemaphoreSlim _registrationLock = new(1, 1);

    public static HaOAuthCredentials? Credentials { get; private set; }
    public static HaClient? Client { get; private set; }
    public static MobileAppRegistration? Registration { get; private set; }

    internal static async Task LoadRegistrationAsync() => Registration = await MobileAppRegistrationStore.LoadAsync();

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
        catch (HaRefreshTokenInvalidException)
        {
            // The refresh token itself was rejected (revoked, HA reinstalled, etc.) — this is the
            // one case that actually means the saved session is gone for good.
            Credentials = null;
            await CredentialStore.Current.ClearAsync();
            return false;
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            // Transient: HA isn't reachable yet. This runs at app startup, which on a boot-with-
            // Windows install is routinely *before* the network (or HA itself) is up, so treating
            // it like a dead session threw the user back to the sign-in screen every morning.
            // Keep the saved session and retry on the usual backoff instead.
            ScheduleRefreshRetry();
            return false;
        }

        try
        {
            await EstablishClientAsync();
            ScheduleRefresh();
            return true;
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            // Refresh succeeded — the session itself is still valid — but connecting failed for
            // some other reason (HA temporarily unreachable, a network blip during an abrupt
            // restart, etc.). Keep the saved credentials so the next retry/relaunch can still use
            // them instead of forcing the user through a full sign-in again.
            ScheduleRefreshRetry();
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
        StopTimers();

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
            catch (Exception ex) { Log.Swallowed(ex); /* best effort */ }
        }

        Credentials = null;
        Registration = null;
        await CredentialStore.Current.ClearAsync();
        await MobileAppRegistrationStore.ClearAsync();
        AppSettings.RaiseConnectionChanged();
    }

    /// <summary>
    /// Re-applies which entities the client tracks (the tile selection or a widget's entity just
    /// changed) and re-reads their states, so anything newly tracked has a state to show.
    /// </summary>
    internal static async Task RefreshTrackedStatesAsync()
    {
        if (Client is not { } client) return;

        client.StateFilter = AppSettings.BuildStateFilter();
        try { await client.RefreshStatesAsync(); }
        catch (Exception ex) { Log.Swallowed(ex); /* best effort — the flyout retries the load itself and shows its error state if that fails too */ }
    }

    /// <summary>Tells HA about a changed device name. A no-op until this machine has registered.</summary>
    internal static async Task RenameDeviceAsync(string deviceName)
    {
        if (Registration is null || Credentials is null) return;

        // update_registration edits the existing device in place. Calling RegisterAsync
        // again here was the previous (wrong) approach — HA's mobile_app config flow has
        // no dedup logic and unconditionally creates a brand-new device on every call to
        // POST /api/mobile_app/registrations, even with an identical device_id/app_id.
        // That was silently spawning a duplicate device in HA on every rename.
        try
        {
            await MobileAppClient.UpdateRegistrationAsync(Credentials.ToConnectionSettings(), Registration.WebhookId, deviceName);
        }
        catch (MobileAppWebhookNotFoundException)
        {
            // The device really was deleted — this is the one legitimate case for a fresh registration.
            await ForceReregisterAsync();
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            // best effort — HA keeps showing the old device name until this succeeds
        }
    }

    private static async Task EstablishClientAsync()
    {
        if (Client is not null)
            await Client.DisposeAsync();

        var client = new HaClient(Credentials!.ToConnectionSettings()) { StateFilter = AppSettings.BuildStateFilter() };
        // DisposeAsync (sign-out, or this same method replacing an old client during a
        // scheduled refresh) cancels the receive loop without raising Disconnected, so
        // this only fires for a genuinely unexpected drop — safe to always reconnect on.
        client.ConnectionStateChanged += state =>
        {
            if (state == HaConnectionState.Disconnected)
                _ = ReconnectWithBackoffAsync(client);
        };

        client.NotificationReceived += NotificationRelay.OnNotificationReceived;
        client.CommandReceived += NotificationRelay.OnRemoteCommandReceived;
        client.RestUnauthorized += () => _ = HandleRestUnauthorizedAsync(client);

        Client = client;
        await client.ConnectAsync();

        // Loaded before anyone is told about the new connection, so the flyout can build its tiles
        // straight from client.States instead of each window fetching every entity for itself.
        try { await client.RefreshStatesAsync(); }
        catch (Exception ex) { Log.Swallowed(ex); /* best effort — the flyout retries the load itself and shows its error state if that fails too */ }

        AppSettings.RaiseConnectionChanged();
        SensorPublisher.Update();

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
                catch (Exception ex) { Log.Swallowed(ex); /* best effort — will retry on next reconnect */ }
            }
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
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
                catch (Exception ex) { Log.Swallowed(ex); /* best effort — will retry next health check */ }
            }
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            // transient network error etc. — not evidence the registration itself is gone
        }
    }

    internal static async Task ForceReregisterAsync()
    {
        Registration = null;
        await MobileAppRegistrationStore.ClearAsync();
        await EnsureMobileAppRegisteredAsync();
    }

    /// <summary>
    /// Registers this app as a mobile_app device on the connected HA instance if it
    /// hasn't been already (or if the connected instance changed since last time).
    /// One device registration is reused for the app's whole lifetime on that instance.
    /// </summary>
    internal static async Task EnsureMobileAppRegisteredAsync()
    {
        if (Credentials is null) return;
        if (Registration is not null && Registration.BaseUrl == Credentials.BaseUrl) return;

        await _registrationLock.WaitAsync();
        try
        {
            // Re-check now that we hold the lock: whoever raced us here first may have already
            // finished registering while we were waiting.
            if (Credentials is null) return;
            if (Registration is not null && Registration.BaseUrl == Credentials.BaseUrl) return;

            var deviceId = Registration?.DeviceId ?? Guid.NewGuid().ToString("N");
            var webhookId = await MobileAppClient.RegisterAsync(Credentials.ToConnectionSettings(), deviceId, AppSettings.SensorPrefs.DeviceName);

            Registration = new MobileAppRegistration(deviceId, Credentials.BaseUrl, webhookId, new List<string>());
            await MobileAppRegistrationStore.SaveAsync(Registration);
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            // best effort — SensorPublisher just skips this tick and retries next time
        }
        finally
        {
            _registrationLock.Release();
        }
    }

    private static async Task ReconnectWithBackoffAsync(HaClient failedClient)
    {
        if (!ReferenceEquals(Client, failedClient) || Credentials is null) return;

        var delay = TimeSpan.FromSeconds(2);
        var handshakeRejected = false;
        while (ReferenceEquals(Client, failedClient))
        {
            await Task.Delay(delay);
            if (!ReferenceEquals(Client, failedClient)) return;

            try
            {
                // Refresh first if the token is at or near expiry — a network blip that outlasted
                // the access token's lifetime would otherwise fail the WS handshake and never
                // recover. A failed handshake is a wrong-login strike on HA's side, so it's worth
                // spending a token refresh to avoid one; a still-valid token needs no such call.
                if (Credentials!.ExpiresAtUtc - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(1))
                    await Credentials.RefreshAsync();
                await EstablishClientAsync();
                return;
            }
            catch (HaRefreshTokenInvalidException)
            {
                await ForgetDeadSessionAsync(failedClient);
                return;
            }
            catch (HaAuthFailedException)
            {
                // HA rejected the token at the handshake — a wrong-login strike, not a network
                // problem, so this must never become a retry loop. One more attempt with a
                // deliberately re-minted token, then give up and make the user sign in again;
                // anything beyond that is just feeding HA's ban counter.
                if (handshakeRejected)
                {
                    await ForgetDeadSessionAsync(failedClient);
                    return;
                }

                handshakeRejected = true;
                try { await Credentials!.RefreshAsync(); }
                catch (HaRefreshTokenInvalidException) { await ForgetDeadSessionAsync(failedClient); return; }
                catch (Exception ex) { Log.Swallowed(ex); /* transient — the retry below re-attempts the whole sequence */ }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
            catch (Exception ex)
            {
                Log.Swallowed(ex);
                // Transient failure (HA unreachable, network blip) — keep retrying, but back off
                // much further than a few seconds so a prolonged outage doesn't itself look like
                // a burst of invalid-auth requests to HA's ban protection.
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
        }
    }

    /// <summary>
    /// The refresh token itself is dead (revoked, HA reinstalled, etc.) — retrying can never
    /// succeed, and doing so anyway would just keep hitting HA's /auth/token endpoint forever,
    /// which HA counts toward its IP-ban threshold the same as any other invalid-auth request
    /// (POST /auth/token is decorated with @log_invalid_auth in HA's own auth component — verified
    /// against home-assistant/core source, not assumed). Forget the dead session, stop every timer
    /// that would otherwise keep retrying against it, and let the user sign in again.
    /// </summary>
    private static async Task ForgetDeadSessionAsync(HaClient? clientToDispose)
    {
        StopTimers();

        if (clientToDispose is not null && ReferenceEquals(Client, clientToDispose))
        {
            await clientToDispose.DisposeAsync();
            Client = null;
        }

        Credentials = null;
        Registration = null;
        await CredentialStore.Current.ClearAsync();
        AppSettings.RaiseConnectionChanged();
    }

    private static void StopTimers()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _registrationHealthTimer?.Dispose();
        _registrationHealthTimer = null;
        SensorPublisher.Stop();
    }

    private static TimeSpan _refreshRetryBackoff = TimeSpan.FromSeconds(10);

    private static void ScheduleRefresh()
    {
        _refreshTimer?.Dispose();
        _refreshRetryBackoff = TimeSpan.FromSeconds(10);

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

            // Deliberately does NOT tear the WebSocket down anymore. HA authenticates a WS
            // connection once, at handshake time, and keeps it for the socket's lifetime — it
            // only drops it when the *refresh* token is revoked, never because the access token
            // behind it aged out. Rebuilding the client every ~28 minutes was therefore pure
            // churn, and each rebuild left every holder of the old client (camera tiles, the
            // media widget, the detail flyout) pointed at a disposed connection carrying a dead
            // token — the actual source of the slow 401 trickle that got this machine IP-banned.
            // REST callers now read the refreshed token straight through HaConnectionSettings.
            if (Client is null || Client.ConnectionState != HaConnectionState.Connected)
                await EstablishClientAsync();
            else
                Client.ClearRestUnauthorized();

            ScheduleRefresh();
        }
        catch (HaRefreshTokenInvalidException)
        {
            await ForgetDeadSessionAsync(Client);
        }
        catch (HaAuthFailedException)
        {
            // HA refused a token it had just minted seconds earlier — nothing a retry can fix, and
            // every attempt is another wrong-login strike. Drop the session instead of looping.
            await ForgetDeadSessionAsync(Client);
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            // Transient failure — retry, but with growing backoff instead of ScheduleRefresh's
            // fixed 10-second floor (computed from ExpiresAtUtc, which never advances while
            // refreshes keep failing — that used to mean a dead refresh token got retried roughly
            // every 10 seconds forever, hitting HA's /auth/token endpoint far harder than the
            // WS-reconnect path ever did. This is what was actually driving the IP ban.
            // TODO: surface reconnect failure via tray icon state instead of silently retrying.
            ScheduleRefreshRetry();
        }
    }

    /// <summary>
    /// Re-arms the refresh timer on the growing backoff rather than the schedule derived from
    /// ExpiresAtUtc — which never advances while refreshes keep failing, so it would otherwise pin
    /// retries to its 10-second floor forever.
    /// </summary>
    private static void ScheduleRefreshRetry()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = new Timer(_ => _ = RefreshAndReconnectAsync(), null, _refreshRetryBackoff, Timeout.InfiniteTimeSpan);
        _refreshRetryBackoff = TimeSpan.FromSeconds(Math.Min(_refreshRetryBackoff.TotalSeconds * 2, 300));
    }

    /// <summary>
    /// A 401 from a REST call means the access token this client is sending is already dead —
    /// pull a fresh one immediately instead of waiting for the scheduled refresh, and only then
    /// let the suppressed callers (camera polling) resume. Without this the client would sit
    /// latched-off until the next scheduled refresh.
    /// </summary>
    private static async Task HandleRestUnauthorizedAsync(HaClient client)
    {
        if (!ReferenceEquals(Client, client) || Credentials is null) return;

        try
        {
            await Credentials.RefreshAsync();
            client.ClearRestUnauthorized();
        }
        catch (HaRefreshTokenInvalidException)
        {
            await ForgetDeadSessionAsync(client);
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            // Transient — leave the latch closed; the scheduled refresh clears it once it succeeds.
        }
    }
}
