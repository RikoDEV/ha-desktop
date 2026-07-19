namespace HaDesktop.Core.Ha;

/// <summary>One actionable-notification button (see HA's notify "data.actions"). Text-reply ("behavior: textInput") actions aren't supported — see WindowsNativeNotifier for why.</summary>
public sealed record NotificationAction(string Id, string Title);
