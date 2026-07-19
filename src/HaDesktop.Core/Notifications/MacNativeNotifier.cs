using System.Diagnostics;
using System.Runtime.Versioning;

namespace HaDesktop.Core.Notifications;

[SupportedOSPlatform("macos")]
public sealed class MacNativeNotifier : INativeNotifier
{
    public async Task ShowAsync(string? title, string message)
    {
        // AppleScript string literals need their own quotes/backslashes escaped —
        // this runs via ArgumentList (no shell), so only the AppleScript syntax
        // itself is a concern, not shell injection.
        var script =
            $"display notification \"{Escape(message)}\" with title \"{Escape(title ?? "Home Assistant")}\"";

        var psi = new ProcessStartInfo("osascript") { UseShellExecute = false };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);

        try
        {
            using var process = Process.Start(psi);
            if (process is not null)
                await process.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // osascript always ships with macOS; only fails if something is badly broken.
        }
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
