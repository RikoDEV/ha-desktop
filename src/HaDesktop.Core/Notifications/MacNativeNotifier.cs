using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using HaDesktop.Core.Ha;

namespace HaDesktop.Core.Notifications;

/// <summary>
/// macOS's own `osascript "display notification"` command has no support for images or action
/// buttons at all — that's a genuine limitation of the AppleScript notification API, not
/// something this app can work around without a bundled helper app. If the optional
/// `terminal-notifier` CLI (`brew install terminal-notifier`) is present, it's used instead for
/// the richer notifications (image + buttons); otherwise this falls back to plain AppleScript
/// (title/message/sound only).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacNativeNotifier : INativeNotifier
{
    public async Task<string?> ShowAsync(string? title, string message, byte[]? imageBytes, IReadOnlyList<NotificationAction> actions, bool silent)
    {
        if (imageBytes is not null || actions.Count > 0)
        {
            var result = await TryShowWithTerminalNotifierAsync(title, message, imageBytes, actions, silent);
            if (result.Handled) return result.ActionId;
            // terminal-notifier isn't installed — fall through to the plain AppleScript
            // notification below so the user at least sees the title/message/sound.
        }

        return await ShowWithAppleScriptAsync(title, message, silent);
    }

    private async Task<(bool Handled, string? ActionId)> TryShowWithTerminalNotifierAsync(
        string? title, string message, byte[]? imageBytes, IReadOnlyList<NotificationAction> actions, bool silent)
    {
        string? imagePath = null;
        try
        {
            var psi = new ProcessStartInfo("terminal-notifier")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-title");
            psi.ArgumentList.Add(title ?? "Home Assistant");
            psi.ArgumentList.Add("-message");
            psi.ArgumentList.Add(message);
            psi.ArgumentList.Add("-json");
            if (!silent)
            {
                psi.ArgumentList.Add("-sound");
                psi.ArgumentList.Add("default");
            }

            if (imageBytes is not null)
            {
                imagePath = Path.Combine(Path.GetTempPath(), $"hadesktop-notif-{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(imagePath, imageBytes);
                psi.ArgumentList.Add("-contentImage");
                psi.ArgumentList.Add(imagePath);
            }

            if (actions.Count > 0)
            {
                psi.ArgumentList.Add("-actions");
                psi.ArgumentList.Add(string.Join(",", actions.Select(a => a.Title)));
            }

            using var process = Process.Start(psi);
            if (process is null) return (false, null);

            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // terminal-notifier's -json output reports which button's *title* was clicked
            // (activationValue), not an id — map it back to the action the caller gave us.
            string? clickedTitle = null;
            try
            {
                using var doc = JsonDocument.Parse(stdout);
                if (doc.RootElement.TryGetProperty("activationValue", out var value))
                    clickedTitle = value.GetString();
            }
            catch (JsonException) { /* unexpected output shape from an unusual terminal-notifier version — treat as no action */ }

            var actionId = clickedTitle is null ? null : actions.FirstOrDefault(a => a.Title == clickedTitle)?.Id;
            return (true, actionId);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, null); // terminal-notifier not installed
        }
        finally
        {
            if (imagePath is not null)
            {
                try { File.Delete(imagePath); } catch { /* best effort cleanup */ }
            }
        }
    }

    private static async Task<string?> ShowWithAppleScriptAsync(string? title, string message, bool silent)
    {
        // AppleScript string literals need their own quotes/backslashes escaped —
        // this runs via ArgumentList (no shell), so only the AppleScript syntax
        // itself is a concern, not shell injection.
        var script = $"display notification \"{Escape(message)}\" with title \"{Escape(title ?? "Home Assistant")}\"";
        if (!silent) script += " sound name \"Glass\"";

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

        return null; // plain AppleScript notifications have no buttons to report back
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
