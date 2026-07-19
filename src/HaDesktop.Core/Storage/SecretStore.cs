using System.Runtime.InteropServices;

namespace HaDesktop.Core.Storage;

public static class SecretStore
{
    public static ISecretStore Current { get; } = Create();

    private static ISecretStore Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new WindowsSecretStore();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new MacSecretStore();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return new LinuxSecretStore();
        return new NullSecretStore();
    }
}
