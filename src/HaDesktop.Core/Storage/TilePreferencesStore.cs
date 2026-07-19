using System.Text.Json;

namespace HaDesktop.Core.Storage;

/// <summary>Which entities the user has chosen to show as quick-toggle tiles, and any label/icon overrides. Not a secret, so a plain JSON file is fine.</summary>
public static class TilePreferencesStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaDesktop", "tiles.json");

    public static async Task SaveAsync(IReadOnlyList<TileConfig> tiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(tiles));
    }

    public static async Task<List<TileConfig>> LoadAsync()
    {
        if (!File.Exists(FilePath))
            return new List<TileConfig>();

        try
        {
            var json = await File.ReadAllTextAsync(FilePath);
            return JsonSerializer.Deserialize<List<TileConfig>>(json) ?? new List<TileConfig>();
        }
        catch (JsonException)
        {
            return new List<TileConfig>();
        }
    }
}
