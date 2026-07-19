namespace HaDesktop.Core.Storage;

/// <summary>Whether to display incoming Home Assistant push notifications as native OS notifications. On by default.</summary>
public static class NotificationPreferencesStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaDesktop", "notifications-disabled.flag");

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
