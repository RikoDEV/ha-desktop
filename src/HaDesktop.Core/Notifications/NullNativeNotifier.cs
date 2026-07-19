using HaDesktop.Core.Ha;

namespace HaDesktop.Core.Notifications;

public sealed class NullNativeNotifier : INativeNotifier
{
    public Task<string?> ShowAsync(string? title, string message, byte[]? imageBytes, IReadOnlyList<NotificationAction> actions, bool silent) =>
        Task.FromResult<string?>(null);
}
