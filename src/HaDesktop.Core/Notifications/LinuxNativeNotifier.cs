using System.Diagnostics;
using System.Runtime.Versioning;
using HaDesktop.Core.Ha;

namespace HaDesktop.Core.Notifications;

/// <summary>
/// Uses `notify-send` (libnotify) — present on virtually all desktop Linux distros with
/// GNOME/KDE/XFCE. Images and actions both depend on the notification daemon actually
/// implementing those hints/capabilities (most mainstream ones do); a daemon that doesn't
/// just ignores what it can't show, so this degrades gracefully rather than failing.
/// "Sound" isn't controllable per-notification via notify-send at all — that's entirely up
/// to the daemon's own theme/config — so <paramref name="silent"/> is a no-op here.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxNativeNotifier : INativeNotifier
{
    public async Task<string?> ShowAsync(string? title, string message, byte[]? imageBytes, IReadOnlyList<NotificationAction> actions, bool silent)
    {
        string? iconPath = null;
        try
        {
            if (imageBytes is not null)
            {
                iconPath = Path.Combine(Path.GetTempPath(), $"hadesktop-notif-{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(iconPath, imageBytes);
            }

            var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false, RedirectStandardOutput = true };
            psi.ArgumentList.Add(title ?? "Home Assistant");
            psi.ArgumentList.Add(message);
            psi.ArgumentList.Add("--app-name=HA Desktop");
            if (iconPath is not null)
            {
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(iconPath);
            }

            // --wait makes notify-send block until the notification is dismissed/expires/an
            // action is clicked, printing the clicked action's key to stdout — without it,
            // the process exits immediately and we'd have no way to learn which button (if
            // any) was pressed.
            if (actions.Count > 0)
            {
                psi.ArgumentList.Add("--wait");
                foreach (var action in actions)
                {
                    psi.ArgumentList.Add("--action");
                    psi.ArgumentList.Add($"{action.Id}={action.Title}");
                }
            }

            using var process = Process.Start(psi);
            if (process is null) return null;

            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var chosen = stdout.Trim();
            return chosen.Length > 0 ? chosen : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // notify-send / libnotify not installed — nothing sensible to fall back to.
            return null;
        }
        finally
        {
            if (iconPath is not null)
            {
                try { File.Delete(iconPath); } catch { /* best effort cleanup */ }
            }
        }
    }
}
