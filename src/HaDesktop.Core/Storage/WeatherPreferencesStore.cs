using System.Text.Json;

namespace HaDesktop.Core.Storage;

public static class WeatherPreferencesStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaDesktop", "weather.json");

    public static async Task SaveAsync(WeatherPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(preferences));
    }

    public static async Task<WeatherPreferences> LoadAsync()
    {
        if (!File.Exists(FilePath))
            return WeatherPreferences.Default;

        try
        {
            var json = await File.ReadAllTextAsync(FilePath);
            return JsonSerializer.Deserialize<WeatherPreferences>(json) ?? WeatherPreferences.Default;
        }
        catch (JsonException)
        {
            return WeatherPreferences.Default;
        }
    }
}
