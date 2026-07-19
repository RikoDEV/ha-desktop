using System.Text.Json;

namespace HaDesktop.Core.Storage;

public static class CredentialStore
{
    public static ICredentialStore Current { get; } = new SecretBackedCredentialStore(SecretStore.Current);

    /// <summary>Adapts the generic <see cref="ISecretStore"/> to the OAuth-specific <see cref="ICredentialStore"/> shape, so callers don't need to know about JSON (de)serialization.</summary>
    private sealed class SecretBackedCredentialStore : ICredentialStore
    {
        private const string Key = "oauth";
        private readonly ISecretStore _secrets;

        public SecretBackedCredentialStore(ISecretStore secrets) => _secrets = secrets;

        public Task SaveAsync(PersistedHaCredentials credentials) =>
            _secrets.SaveAsync(Key, JsonSerializer.Serialize(credentials));

        public async Task<PersistedHaCredentials?> LoadAsync()
        {
            var json = await _secrets.LoadAsync(Key);
            if (json is null) return null;

            try
            {
                return JsonSerializer.Deserialize<PersistedHaCredentials>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public Task ClearAsync() => _secrets.ClearAsync(Key);
    }
}
