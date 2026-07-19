using System.Text.Json;

namespace HaDesktop.Core.Storage;

/// <summary>Which local sensors the user opted in to sharing with Home Assistant, and under what device name. Not a secret.</summary>
public static class SensorPreferencesStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaDesktop", "sensor-preferences.json");

    public static async Task SaveAsync(SensorPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(preferences));
    }

    public static async Task<SensorPreferences> LoadAsync()
    {
        if (!File.Exists(FilePath))
            return SensorPreferences.Default;

        try
        {
            var json = await File.ReadAllTextAsync(FilePath);
            return JsonSerializer.Deserialize<SensorPreferences>(json) ?? SensorPreferences.Default;
        }
        catch (JsonException)
        {
            return SensorPreferences.Default;
        }
    }
}
