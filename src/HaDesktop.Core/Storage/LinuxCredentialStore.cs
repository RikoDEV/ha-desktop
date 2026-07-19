using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace HaDesktop.Core.Storage;

/// <summary>
/// Stores the refresh token via the freedesktop Secret Service (GNOME
/// Keyring / KWallet) using the `secret-tool` CLI from libsecret-tools.
/// Falls back to "no saved session" if libsecret isn't installed, rather
/// than ever writing the token to a plain file.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxCredentialStore : ICredentialStore
{
    private const string ServiceAttr = "hadesktop";
    private const string AccountAttr = "default";

    public async Task SaveAsync(PersistedHaCredentials credentials)
    {
        var json = JsonSerializer.Serialize(credentials);
        await RunSecretToolAsync(
            stdin: json,
            "store", "--label=HA Desktop credentials",
            "service", ServiceAttr, "account", AccountAttr);
    }

    public async Task<PersistedHaCredentials?> LoadAsync()
    {
        var (exitCode, stdout) = await RunSecretToolAsync(
            stdin: null,
            "lookup", "service", ServiceAttr, "account", AccountAttr);
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
        await RunSecretToolAsync(stdin: null, "clear", "service", ServiceAttr, "account", AccountAttr);
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
