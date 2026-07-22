using System.Text.Json;

namespace HaDesktop.Core.Storage;

public static class FlyoutWindowPreferencesStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaDesktop", "window.json");

    public static async Task SaveAsync(FlyoutWindowPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(preferences));
    }

    public static async Task<FlyoutWindowPreferences> LoadAsync()
    {
        if (!File.Exists(FilePath))
            return FlyoutWindowPreferences.Default;

        try
        {
            var json = await File.ReadAllTextAsync(FilePath);
            return JsonSerializer.Deserialize<FlyoutWindowPreferences>(json) ?? FlyoutWindowPreferences.Default;
        }
        catch (JsonException)
        {
            return FlyoutWindowPreferences.Default;
        }
    }
}
