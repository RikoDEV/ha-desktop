using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using HaDesktop.Core.Diagnostics;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Notifications;
using HaDesktop.Core.Sensors;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

/// <summary>What happens to a push from Home Assistant's Local Push channel once it arrives: shown as a native notification, or executed as a remote command.</summary>
public static class NotificationRelay
{
    private const int HistoryLimit = 10;

    /// <summary>Most recent notifications received via HA's Local Push channel, newest first. In-memory only, capped at 10.</summary>
    public static List<NotificationHistoryEntry> RecentNotifications { get; } = new();

    public static Task SendTestNotificationAsync() =>
        NativeNotifier.Current.ShowAsync(Loc.Instance.Tr("Notification.TestTitle"), Loc.Instance.Tr("Notification.TestBody"), null, Array.Empty<NotificationAction>(), silent: false);

    internal static void OnNotificationReceived(HaNotification notification)
    {
        RecentNotifications.Insert(0, new NotificationHistoryEntry(notification.Title, notification.Message, DateTimeOffset.Now));
        while (RecentNotifications.Count > HistoryLimit)
            RecentNotifications.RemoveAt(RecentNotifications.Count - 1);

        if (!AppSettings.NotificationsEnabled) return;
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
    internal static void OnRemoteCommandReceived(HaRemoteCommand command)
    {
        if (!AppSettings.SensorPrefs.ShareVolume) return;

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

        if (actionId is null) return;

        // A "uri" action navigates locally instead of triggering an HA-side automation — this is
        // the path for notifiers that report the click back in-process (macOS/Linux); Windows'
        // toast click goes through a relaunch instead (see Program.cs), which has its own copy of
        // this same uri-vs-report branch since it has no access to this notification's Actions list.
        var matchedAction = notification.Actions?.FirstOrDefault(a => a.Id == actionId);
        if (matchedAction?.Uri is { } uri)
        {
            ShellLauncher.TryOpen(uri);
            return;
        }

        if (HaSession.Credentials is not { } credentials || HaSession.Registration is not { } registration) return;

        try
        {
            await HaSession.MobileAppClient.FireEventAsync(credentials.ToConnectionSettings(), registration.WebhookId,
                "mobile_app_notification_action", new JsonObject { ["action"] = actionId });
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            // best effort — the button click still visually registered for the user even if HA never hears about it
        }
    }
}
