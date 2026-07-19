namespace HaDesktop.Core.Notifications;

public sealed class NullNativeNotifier : INativeNotifier
{
    public Task ShowAsync(string? title, string message) => Task.CompletedTask;
}
