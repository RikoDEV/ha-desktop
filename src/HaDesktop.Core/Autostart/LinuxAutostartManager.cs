using System.Runtime.Versioning;

namespace HaDesktop.Core.Autostart;

[SupportedOSPlatform("linux")]
public sealed class LinuxAutostartManager : IAutostartManager
{
    private static readonly string DesktopFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), // ~/.config
        "autostart", "hadesktop.desktop");

    public Task<bool> IsEnabledAsync() => Task.FromResult(File.Exists(DesktopFilePath));

    public async Task SetEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine the running executable's path.");

            var desktopEntry =
                $"""
                [Desktop Entry]
                Type=Application
                Name=HA Desktop
                Exec="{exePath}"
                Terminal=false
                X-GNOME-Autostart-enabled=true
                """;

            Directory.CreateDirectory(Path.GetDirectoryName(DesktopFilePath)!);
            await File.WriteAllTextAsync(DesktopFilePath, desktopEntry);
        }
        else if (File.Exists(DesktopFilePath))
        {
            File.Delete(DesktopFilePath);
        }
    }
}
