using System.Diagnostics;
using System.Runtime.Versioning;

namespace HaDesktop.Core.Notifications;

/// <summary>Uses `notify-send` (libnotify) — present on virtually all desktop Linux distros with GNOME/KDE/XFCE.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxNativeNotifier : INativeNotifier
{
    public async Task ShowAsync(string? title, string message)
    {
        var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false };
        psi.ArgumentList.Add(title ?? "Home Assistant");
        psi.ArgumentList.Add(message);
        psi.ArgumentList.Add("--app-name=HA Desktop");

        try
        {
            using var process = Process.Start(psi);
            if (process is not null)
                await process.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // notify-send / libnotify not installed — nothing sensible to fall back to.
        }
    }
}
