using System.Diagnostics;
using System.Runtime.Versioning;

namespace HaDesktop.Core.Autostart;

[SupportedOSPlatform("macos")]
public sealed class MacAutostartManager : IAutostartManager
{
    private static readonly string PlistPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", "com.hadesktop.app.plist");

    public Task<bool> IsEnabledAsync() => Task.FromResult(File.Exists(PlistPath));

    public async Task SetEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine the running executable's path.");

            var plist =
                $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>com.hadesktop.app</string>
                    <key>ProgramArguments</key>
                    <array>
                        <string>{exePath}</string>
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                </dict>
                </plist>
                """;

            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            await File.WriteAllTextAsync(PlistPath, plist);
            await RunLaunchctlAsync("load", PlistPath);
        }
        else
        {
            if (File.Exists(PlistPath))
            {
                await RunLaunchctlAsync("unload", PlistPath);
                File.Delete(PlistPath);
            }
        }
    }

    private static async Task RunLaunchctlAsync(string verb, string plistPath)
    {
        var psi = new ProcessStartInfo("launchctl") { UseShellExecute = false };
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(plistPath);

        try
        {
            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // launchctl always ships with macOS; if this ever fails, the plist file
            // itself (RunAtLoad) still takes effect on the next actual login.
        }
    }
}
