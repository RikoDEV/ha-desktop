namespace HaDesktop.Core.Ha;

/// <summary>
/// One actionable-notification button (see HA's notify "data.actions"). Text-reply
/// ("behavior: textInput") actions aren't supported — see WindowsNativeNotifier for why.
/// When <see cref="Uri"/> is set, clicking the button opens that URI locally instead of
/// reporting the action back to HA as a "mobile_app_notification_action" event — matching the
/// official Companion apps' behavior for a "uri" action.
/// </summary>
public sealed record NotificationAction(string Id, string Title, string? Uri = null);
