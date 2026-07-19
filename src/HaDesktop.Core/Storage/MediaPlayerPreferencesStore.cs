using System.Text.Json;

namespace HaDesktop.Core.Storage;

public static class MediaPlayerPreferencesStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaDesktop", "media-player.json");

    public static async Task SaveAsync(MediaPlayerPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(preferences));
    }

    public static async Task<MediaPlayerPreferences> LoadAsync()
    {
        if (!File.Exists(FilePath))
            return MediaPlayerPreferences.Default;

        try
        {
            var json = await File.ReadAllTextAsync(FilePath);
            return JsonSerializer.Deserialize<MediaPlayerPreferences>(json) ?? MediaPlayerPreferences.Default;
        }
        catch (JsonException)
        {
            return MediaPlayerPreferences.Default;
        }
    }
}
