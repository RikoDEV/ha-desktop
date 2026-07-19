using System.Runtime.InteropServices;

namespace HaDesktop.Core.Autostart;

public static class AutostartManager
{
    public static IAutostartManager Current { get; } = Create();

    private static IAutostartManager Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new WindowsAutostartManager();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new MacAutostartManager();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return new LinuxAutostartManager();
        return new NullAutostartManager();
    }
}
