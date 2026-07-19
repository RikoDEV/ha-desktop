using System.Diagnostics;
using System.Runtime.Versioning;

namespace HaDesktop.Core.Notifications;

/// <summary>
/// Shows a real Windows Action Center toast via a short PowerShell script using
/// the WinRT ToastNotificationManager — no extra NuGet package or app-packaging
/// identity required, at the cost of the toast showing under a generic sender
/// name rather than "HA Desktop" specifically (a known limitation of unpackaged
/// apps using this approach).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNativeNotifier : INativeNotifier
{
    private const string Script =
        """
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
        $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $textNodes = $template.GetElementsByTagName('text')
        $textNodes.Item(0).AppendChild($template.CreateTextNode($env:HA_DESKTOP_TITLE)) | Out-Null
        $textNodes.Item(1).AppendChild($template.CreateTextNode($env:HA_DESKTOP_MESSAGE)) | Out-Null
        $toast = [Windows.UI.Notifications.ToastNotification]::new($template)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('HA Desktop').Show($toast)
        """;

    public async Task ShowAsync(string? title, string message)
    {
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
    }
}
