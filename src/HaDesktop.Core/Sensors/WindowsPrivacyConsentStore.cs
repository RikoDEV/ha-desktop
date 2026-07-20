using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HaDesktop.Core.Sensors;

/// <summary>
/// Reads the same registry data Windows itself uses to show the taskbar camera/microphone-in-use
/// indicators — HKCU\...\CapabilityAccessManager\ConsentStore\{webcam,microphone}, one subkey per
/// app (packaged apps keyed by package family name directly, unpackaged ones under "NonPackaged"),
/// each with LastUsedTimeStart/LastUsedTimeStop (FILETIME ticks). An app currently holds the device
/// if its most recent Start has no matching Stop yet. This is deliberately not based on an audio
/// peak-level check (unlike <see cref="WindowsAudioEndpoint.IsOutputActive"/>) — ambient microphone
/// noise would make that read "in use" even with nothing actually recording.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsPrivacyConsentStore
{
    private const string ConsentStoreRoot = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    public static bool? IsCameraInUse() => IsCapabilityInUse("webcam");
    public static bool? IsMicrophoneInUse() => IsCapabilityInUse("microphone");

    private static bool? IsCapabilityInUse(string capability)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey($@"{ConsentStoreRoot}\{capability}");
            if (root is null) return null; // no record at all on this machine — can't determine, not "not in use"

            if (AnySubkeyInUse(root)) return true;

            using var nonPackaged = root.OpenSubKey("NonPackaged");
            return nonPackaged is not null && AnySubkeyInUse(nonPackaged);
        }
        catch
        {
            return null;
        }
    }

    private static bool AnySubkeyInUse(RegistryKey key)
    {
        foreach (var name in key.GetSubKeyNames())
        {
            if (name == "NonPackaged") continue;

            using var sub = key.OpenSubKey(name);
            if (sub is null) continue;

            var start = sub.GetValue("LastUsedTimeStart") as long?;
            var stop = sub.GetValue("LastUsedTimeStop") as long?;
            if (start is > 0 && (stop is null or 0 || start > stop))
                return true;
        }
        return false;
    }
}
