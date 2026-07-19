using System.Text.Json;

namespace HaDesktop.Core.Storage;

public static class AppearancePreferencesStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaDesktop", "appearance.json");

    public static async Task SaveAsync(AppearancePreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(preferences));
    }

    public static async Task<AppearancePreferences> LoadAsync()
    {
        if (!File.Exists(FilePath))
            return AppearancePreferences.Default;

        try
        {
            var json = await File.ReadAllTextAsync(FilePath);
            return JsonSerializer.Deserialize<AppearancePreferences>(json) ?? AppearancePreferences.Default;
        }
        catch (JsonException)
        {
            return AppearancePreferences.Default;
        }
    }
}
