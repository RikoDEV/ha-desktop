namespace HaDesktop.Core.Storage;

/// <summary>Used when no OS-native secret store is available.</summary>
public sealed class NullSecretStore : ISecretStore
{
    public Task SaveAsync(string key, string secret) => Task.CompletedTask;
    public Task<string?> LoadAsync(string key) => Task.FromResult<string?>(null);
    public Task ClearAsync(string key) => Task.CompletedTask;
}
