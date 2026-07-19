using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace HaDesktop.Core.Storage;

/// <summary>
/// Encrypts each named secret with DPAPI (CurrentUser scope) — the same
/// primitive Windows Credential Manager itself is built on — and stores it
/// under %LOCALAPPDATA%. Only this Windows user account can decrypt it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSecretStore : ISecretStore
{
    private static readonly string SecretsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaDesktop", "secrets");

    public Task SaveAsync(string key, string secret)
    {
        Directory.CreateDirectory(SecretsDir);

        var bytes = Encoding.UTF8.GetBytes(secret);
        var encrypted = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(key), encrypted);
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
            return Task.FromResult<string?>(null);

        try
        {
            var encrypted = File.ReadAllBytes(path);
            var bytes = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
        }
        catch (CryptographicException)
        {
            // Undecryptable (different user profile, corrupted file, etc.) — treat as unset.
            return Task.FromResult<string?>(null);
        }
    }

    public Task ClearAsync(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private static string PathFor(string key) => Path.Combine(SecretsDir, $"{Sanitize(key)}.dat");

    private static string Sanitize(string key) =>
        string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
