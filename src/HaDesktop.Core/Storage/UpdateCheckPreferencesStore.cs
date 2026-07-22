namespace HaDesktop.Core.Storage;

/// <summary>Whether to check GitHub Releases for a newer app version. On by default.</summary>
public static class UpdateCheckPreferencesStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaDesktop", "update-check-disabled.flag");

    public static Task SaveAsync(bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        if (!enabled)
            File.WriteAllText(FilePath, "1");
        else if (File.Exists(FilePath))
            File.Delete(FilePath);
        return Task.CompletedTask;
    }

    public static Task<bool> LoadAsync() => Task.FromResult(!File.Exists(FilePath));
}
