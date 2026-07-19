using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace HaDesktop.Core.Storage;

/// <summary>
/// Stores the refresh token in the macOS login Keychain via the `security`
/// CLI — the same tool most command-line HA/dev utilities use; avoids
/// needing a native Security.framework P/Invoke binding for a single value.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacCredentialStore : ICredentialStore
{
    private const string Service = "com.hadesktop.oauth";
    private const string Account = "default";

    public async Task SaveAsync(PersistedHaCredentials credentials)
    {
        var json = JsonSerializer.Serialize(credentials);
        await RunSecurityAsync("add-generic-password", "-a", Account, "-s", Service, "-w", json, "-U");
    }

    public async Task<PersistedHaCredentials?> LoadAsync()
    {
        var (exitCode, stdout) = await RunSecurityAsync("find-generic-password", "-a", Account, "-s", Service, "-w");
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            return null;

        try
        {
            return JsonSerializer.Deserialize<PersistedHaCredentials>(stdout.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task ClearAsync()
    {
        await RunSecurityAsync("delete-generic-password", "-a", Account, "-s", Service);
    }

    private static async Task<(int ExitCode, string Stdout)> RunSecurityAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout);
    }
}
