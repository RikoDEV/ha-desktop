using System.Diagnostics;
using System.Runtime.Versioning;

namespace HaDesktop.Core.Storage;

/// <summary>
/// Stores secrets via the freedesktop Secret Service (GNOME Keyring / KWallet)
/// using the `secret-tool` CLI from libsecret-tools. Each secret is keyed by
/// an account name under one shared service. Falls back to "no saved value"
/// if libsecret isn't installed, rather than ever writing a secret to a plain file.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSecretStore : ISecretStore
{
    private const string ServiceAttr = "hadesktop";

    public async Task SaveAsync(string key, string secret)
    {
        await RunSecretToolAsync(
            stdin: secret,
            "store", "--label=HA Desktop", "service", ServiceAttr, "account", key);
    }

    public async Task<string?> LoadAsync(string key)
    {
        var (exitCode, stdout) = await RunSecretToolAsync(
            stdin: null,
            "lookup", "service", ServiceAttr, "account", key);
        return exitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim() : null;
    }

    public async Task ClearAsync(string key)
    {
        await RunSecretToolAsync(stdin: null, "clear", "service", ServiceAttr, "account", key);
    }

    private static async Task<(int ExitCode, string Stdout)> RunSecretToolAsync(string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi)!;
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // secret-tool not installed.
            return (-1, string.Empty);
        }
    }
}
