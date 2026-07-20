using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using HaDesktop.Core.Ha;
using Microsoft.Win32;

namespace HaDesktop.Core.Notifications;

/// <summary>
/// Shows a real Windows Action Center toast via a short PowerShell script using the WinRT
/// ToastNotificationManager — no extra NuGet package or full app-packaging required. The toast's
/// icon comes from a Start Menu shortcut (see <see cref="WindowsToastShortcut"/>) carrying an
/// AppUserModelID matching the "HA Desktop" identifier passed to CreateToastNotifier below —
/// without a shortcut registering that identity and pointing at this exe's own icon, Windows has
/// nothing to look up and falls back to a generic icon.
///
/// Action buttons use activationType="protocol" (arguments="hadesktop-notify-action:{id}", or
/// "hadesktop-notify-action:{id}?uri={escaped}" for a "uri" action), resolved via
/// <see cref="EnsureProtocolRegistered"/> to a per-user registry entry that reopens this exe.
/// That's deliberate: proper in-app activation (so a click is delivered back to this same running
/// process) needs a registered COM notification activator + AppUserModelID, which in turn needs a
/// packaged app identity this project doesn't have. Protocol activation needs none of that —
/// clicking a button just relaunches HaDesktop.Tray with the action id (and optional uri) as an
/// argument (see Program.cs), which opens the uri or reports the id to HA and exits, at the cost
/// of a brief relaunch instead of an in-process callback. Text-reply ("behavior: textInput")
/// actions aren't supported this way — reading a toast's input field back requires that same
/// proper activation path.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNativeNotifier : INativeNotifier
{
    // Builds on GetTemplateContent's legacy toast templates (ToastText02 / ToastImageAndText02)
    // via DOM manipulation (CreateTextNode/CreateElement/SetAttribute), extending them with
    // <actions>/<audio> rather than hand-building a "ToastGeneric" binding from an XML string.
    // That's not a style choice: a raw ToastGeneric toast from an unregistered ad-hoc
    // CreateToastNotifier(string) call (no packaged app identity/AUMID) silently fails to render
    // at all on this setup — no exception, no toast — while the legacy templates render reliably
    // even without one. Confirmed by testing both directly against a real notifier.
    private const string Script =
        """
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null

        $templateType = if ($env:HA_DESKTOP_IMAGE_PATH) { [Windows.UI.Notifications.ToastTemplateType]::ToastImageAndText02 } else { [Windows.UI.Notifications.ToastTemplateType]::ToastText02 }
        $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent($templateType)

        $textNodes = $template.GetElementsByTagName('text')
        $textNodes.Item(0).AppendChild($template.CreateTextNode($env:HA_DESKTOP_TITLE)) | Out-Null
        $textNodes.Item(1).AppendChild($template.CreateTextNode($env:HA_DESKTOP_MESSAGE)) | Out-Null

        if ($env:HA_DESKTOP_IMAGE_PATH) {
            $slashed = $env:HA_DESKTOP_IMAGE_PATH.Replace('\', '/')
            $imageNode = $template.GetElementsByTagName('image').Item(0)
            $imageNode.SetAttribute('src', "file:///$slashed")
        }

        $toastRoot = $template.DocumentElement

        if ($env:HA_DESKTOP_ACTIONS_JSON) {
            $actions = $env:HA_DESKTOP_ACTIONS_JSON | ConvertFrom-Json
            $actionsEl = $template.CreateElement('actions')
            foreach ($a in $actions) {
                $actionEl = $template.CreateElement('action')
                $actionEl.SetAttribute('content', $a.title)
                $argValue = $a.id
                if ($a.uri) { $argValue = "$($a.id)?uri=$([uri]::EscapeDataString($a.uri))" }
                $actionEl.SetAttribute('arguments', "hadesktop-notify-action:$argValue")
                $actionEl.SetAttribute('activationType', 'protocol')
                $actionsEl.AppendChild($actionEl) | Out-Null
            }
            $toastRoot.AppendChild($actionsEl) | Out-Null
        }

        if ($env:HA_DESKTOP_SILENT -eq 'true') {
            $audioEl = $template.CreateElement('audio')
            $audioEl.SetAttribute('silent', 'true')
            $toastRoot.AppendChild($audioEl) | Out-Null
        }

        $toast = [Windows.UI.Notifications.ToastNotification]::new($template)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('HA Desktop').Show($toast)
        """;

    public async Task<string?> ShowAsync(string? title, string message, byte[]? imageBytes, IReadOnlyList<NotificationAction> actions, bool silent)
    {
        EnsureProtocolRegistered();
        WindowsToastShortcut.EnsureRegistered();

        string? imagePath = null;
        if (imageBytes is not null)
        {
            imagePath = Path.Combine(Path.GetTempPath(), $"hadesktop-notif-{Guid.NewGuid():N}.png");
            await File.WriteAllBytesAsync(imagePath, imageBytes);
            // Not deleted afterwards: the script below returns as soon as the toast is handed to
            // Action Center, which reads the image lazily (e.g. when the notification center
            // panel is opened later) — deleting it right away risks a broken image.
        }

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(Script);
        // Passed via environment, not interpolated into the script text, so
        // notification content can never break out into arbitrary PowerShell.
        psi.Environment["HA_DESKTOP_TITLE"] = title ?? "Home Assistant";
        psi.Environment["HA_DESKTOP_MESSAGE"] = message;
        psi.Environment["HA_DESKTOP_SILENT"] = silent ? "true" : "false";
        if (imagePath is not null)
            psi.Environment["HA_DESKTOP_IMAGE_PATH"] = imagePath;
        if (actions.Count > 0)
            psi.Environment["HA_DESKTOP_ACTIONS_JSON"] = JsonSerializer.Serialize(actions.Select(a => new { id = a.Id, title = a.Title, uri = a.Uri }));

        try
        {
            using var process = Process.Start(psi);
            if (process is not null)
                await process.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // powershell.exe missing — nothing sensible to fall back to.
        }

        // Button clicks are reported back to HA via a relaunch of this exe (Program.cs), not
        // through this call — see the class summary for why.
        return null;
    }

    /// <summary>
    /// Registers a per-user (HKCU, no admin needed) URI scheme that reopens this exe with the
    /// clicked toast action's id as its argument. Idempotent and cheap to call on every notification
    /// — only actually touches the registry the first time, or after the app moves/updates.
    /// </summary>
    private static void EnsureProtocolRegistered()
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null) return;

        var command = $"\"{exePath}\" \"%1\"";
        using var protocolKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{NotificationProtocol.Scheme}");
        using var commandKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{NotificationProtocol.Scheme}\shell\open\command");

        if (protocolKey.GetValue("URL Protocol") is string && (string?)commandKey.GetValue(null) == command)
            return; // already correctly registered

        protocolKey.SetValue(null, "URL:HA Desktop notification action");
        protocolKey.SetValue("URL Protocol", "");
        commandKey.SetValue(null, command);
    }
}
