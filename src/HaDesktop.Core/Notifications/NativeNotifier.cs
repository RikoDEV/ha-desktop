using System.Runtime.InteropServices;

namespace HaDesktop.Core.Notifications;

public static class NativeNotifier
{
    public static INativeNotifier Current { get; } = Create();

    private static INativeNotifier Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new WindowsNativeNotifier();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new MacNativeNotifier();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return new LinuxNativeNotifier();
        return new NullNativeNotifier();
    }
}
