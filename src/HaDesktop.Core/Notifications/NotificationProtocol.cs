namespace HaDesktop.Core.Notifications;

/// <summary>
/// Custom URI scheme used to report Windows toast action-button clicks back to this app (see
/// WindowsNativeNotifier and Program.cs). Deliberately not platform-gated itself — the string
/// constant has no OS dependency, only the registry registration and toast wiring that use it do
/// — so code that only needs the scheme name doesn't trip a platform-compatibility warning for
/// referencing a Windows-only type.
/// </summary>
public static class NotificationProtocol
{
    public const string Scheme = "hadesktop-notify-action";
}
