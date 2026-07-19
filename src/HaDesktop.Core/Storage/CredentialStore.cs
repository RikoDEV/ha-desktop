using System.Runtime.InteropServices;

namespace HaDesktop.Core.Storage;

public static class CredentialStore
{
    public static ICredentialStore Current { get; } = Create();

    private static ICredentialStore Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new WindowsCredentialStore();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new MacCredentialStore();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return new LinuxCredentialStore();
        return new NullCredentialStore();
    }
}
