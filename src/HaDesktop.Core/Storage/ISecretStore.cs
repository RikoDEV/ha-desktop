namespace HaDesktop.Core.Storage;

/// <summary>OS-native secret storage, keyed by a short identifier (e.g. "oauth", "mobile-app-webhook") so multiple independent secrets can share the same backend.</summary>
public interface ISecretStore
{
    Task SaveAsync(string key, string secret);
    Task<string?> LoadAsync(string key);
    Task ClearAsync(string key);
}
