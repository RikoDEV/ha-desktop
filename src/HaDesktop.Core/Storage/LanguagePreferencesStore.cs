using System.Text.Json;

namespace HaDesktop.Core.Storage;

public static class LanguagePreferencesStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaDesktop", "language.json");

    public static async Task SaveAsync(LanguagePreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(preferences));
    }

    public static async Task<LanguagePreferences> LoadAsync()
    {
        if (!File.Exists(FilePath))
            return LanguagePreferences.Default;

        try
        {
            var json = await File.ReadAllTextAsync(FilePath);
            return JsonSerializer.Deserialize<LanguagePreferences>(json) ?? LanguagePreferences.Default;
        }
        catch (JsonException)
        {
            return LanguagePreferences.Default;
        }
    }
}
