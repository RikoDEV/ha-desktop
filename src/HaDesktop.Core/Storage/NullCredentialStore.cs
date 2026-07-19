namespace HaDesktop.Core.Storage;

/// <summary>Used when no OS-native secret store is available — the user just signs in again each launch.</summary>
public sealed class NullCredentialStore : ICredentialStore
{
    public Task SaveAsync(PersistedHaCredentials credentials) => Task.CompletedTask;
    public Task<PersistedHaCredentials?> LoadAsync() => Task.FromResult<PersistedHaCredentials?>(null);
    public Task ClearAsync() => Task.CompletedTask;
}
