namespace HaDesktop.Core.Notifications;

public interface INativeNotifier
{
    Task ShowAsync(string? title, string message);
}
