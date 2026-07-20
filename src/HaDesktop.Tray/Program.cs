using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Notifications;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows() && args.Length > 0
            && args[0].StartsWith(NotificationProtocol.Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            HandleNotificationActionAsync(args[0]).GetAwaiter().GetResult();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Entry point when the user clicks an action button on a Windows toast (see
    /// WindowsNativeNotifier's class summary for why this needs a relaunch instead of an
    /// in-process callback): Windows reopens this exe with the clicked action's protocol
    /// argument, "{id}" or "{id}?uri={escaped}" for a "uri" action. A uri action opens locally
    /// (browser, or whatever handles that scheme) instead of reporting anything to HA — same as
    /// AppSettings.ShowAndReportActionAsync does for the macOS/Linux notifiers, which report a
    /// click back in-process instead of via a relaunch and so have their own copy of this branch.
    /// </summary>
    private static async Task HandleNotificationActionAsync(string protocolArgument)
    {
        try
        {
            var payload = protocolArgument[(NotificationProtocol.Scheme.Length + 1)..];

            var queryIndex = payload.IndexOf("?uri=", StringComparison.Ordinal);
            if (queryIndex >= 0)
            {
                var actionUri = Uri.UnescapeDataString(payload[(queryIndex + "?uri=".Length)..]);
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(actionUri) { UseShellExecute = true }); }
                catch { /* best effort — no default handler for this URI, nothing sensible to do */ }
                return;
            }

            var actionId = payload;

            var saved = await CredentialStore.Current.LoadAsync();
            var registration = await MobileAppRegistrationStore.LoadAsync();
            if (saved is null || registration is null) return;

            var credentials = new HaOAuthCredentials
            {
                BaseUrl = saved.BaseUrl,
                ClientId = saved.ClientId,
                RefreshToken = saved.RefreshToken,
                AccessToken = string.Empty,
                ExpiresAtUtc = DateTimeOffset.MinValue, // force an immediate refresh before use
            };
            await credentials.RefreshAsync();

            using var http = new HttpClient();
            var mobileAppClient = new HaMobileAppClient(http);
            await mobileAppClient.FireEventAsync(credentials.ToConnectionSettings(), registration.WebhookId,
                "mobile_app_notification_action", new JsonObject { ["action"] = actionId });
        }
        catch
        {
            // best effort — there's no UI in this relaunch to report a failure through
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
