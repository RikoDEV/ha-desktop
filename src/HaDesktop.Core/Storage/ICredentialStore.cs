namespace HaDesktop.Core.Storage;

/// <summary>OS-native secret storage for the HA refresh token, so the browser login only has to happen once per machine.</summary>
public interface ICredentialStore
{
    Task SaveAsync(PersistedHaCredentials credentials);
    Task<PersistedHaCredentials?> LoadAsync();
    Task ClearAsync();
}
