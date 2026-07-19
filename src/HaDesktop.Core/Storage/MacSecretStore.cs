using System.Diagnostics;
using System.Runtime.Versioning;

namespace HaDesktop.Core.Storage;

/// <summary>
/// Stores secrets in the macOS login Keychain via the `security` CLI — the
/// same tool most command-line HA/dev utilities use; avoids needing a native
/// Security.framework P/Invoke binding. Each secret is keyed by an account
/// name under one shared service.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacSecretStore : ISecretStore
{
    private const string Service = "com.hadesktop.secrets";

    public async Task SaveAsync(string key, string secret)
    {
        // `security add-generic-password` has no documented way to read -w from stdin, so the
        // secret is unavoidably visible in this process's argument list for the brief lifetime of
        // the `security` child process — another local process with sufficient privilege could
        // observe it via `ps`/Activity Monitor in that window. Accepted tradeoff of shelling out
        // to the CLI instead of a native Security.framework binding; the Linux store below avoids
        // this by passing the secret over stdin instead, which `secret-tool` does support.
        await RunSecurityAsync("add-generic-password", "-a", key, "-s", Service, "-w", secret, "-U");
    }

    public async Task<string?> LoadAsync(string key)
    {
        var (exitCode, stdout) = await RunSecurityAsync("find-generic-password", "-a", key, "-s", Service, "-w");
        return exitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim() : null;
    }

    public async Task ClearAsync(string key)
    {
        await RunSecurityAsync("delete-generic-password", "-a", key, "-s", Service);
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
