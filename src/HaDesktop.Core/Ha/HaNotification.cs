namespace HaDesktop.Core.Ha;

public sealed record HaNotification(
    string? Title,
    string Message,
    byte[]? ImageBytes = null,
    IReadOnlyList<NotificationAction>? Actions = null,
    bool Silent = false);
