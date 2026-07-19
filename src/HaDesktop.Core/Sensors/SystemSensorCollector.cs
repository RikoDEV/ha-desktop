using System.Runtime.InteropServices;

namespace HaDesktop.Core.Sensors;

public static class SystemSensorCollector
{
    public static ISystemSensorCollector Current { get; } = Create();

    private static ISystemSensorCollector Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new WindowsSensorCollector();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return new LinuxSensorCollector();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new MacSensorCollector();
        return new NullSensorCollector();
    }
}
