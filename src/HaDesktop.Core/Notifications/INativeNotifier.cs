using HaDesktop.Core.Ha;

namespace HaDesktop.Core.Notifications;

public interface INativeNotifier
{
    /// <summary>
    /// Shows a native notification, optionally with an inline image and action buttons.
    /// Returns the id of the action button the user clicked, or null if none was clicked
    /// (dismissed, timed out, or the platform doesn't support actions) — see each
    /// implementation for what's actually achievable on that OS.
    /// </summary>
    Task<string?> ShowAsync(string? title, string message, byte[]? imageBytes, IReadOnlyList<NotificationAction> actions, bool silent);
}
