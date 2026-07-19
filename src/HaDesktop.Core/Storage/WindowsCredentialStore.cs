using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace HaDesktop.Core.Storage;

/// <summary>
/// Encrypts the refresh token with DPAPI (CurrentUser scope) — the same
/// primitive Windows Credential Manager itself is built on — and stores it
/// under %LOCALAPPDATA%. Only this Windows user account can decrypt it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaDesktop", "credentials.dat");

    public Task SaveAsync(PersistedHaCredentials credentials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        var json = JsonSerializer.SerializeToUtf8Bytes(credentials);
        var encrypted = ProtectedData.Protect(json, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, encrypted);
        return Task.CompletedTask;
    }

    public Task<PersistedHaCredentials?> LoadAsync()
    {
        if (!File.Exists(FilePath))
            return Task.FromResult<PersistedHaCredentials?>(null);

        try
        {
            var encrypted = File.ReadAllBytes(FilePath);
            var json = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Task.FromResult(JsonSerializer.Deserialize<PersistedHaCredentials>(json));
        }
        catch (CryptographicException)
        {
            // Undecryptable (different user profile, corrupted file, etc.) — treat as no saved session.
            return Task.FromResult<PersistedHaCredentials?>(null);
        }
    }

    public Task ClearAsync()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
        return Task.CompletedTask;
    }
}
