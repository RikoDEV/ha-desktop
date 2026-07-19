using System.Text.Json;

namespace HaDesktop.Core.Storage;

/// <summary>
/// The webhook_id is a write-only credential scoped to this one device's sensors
/// (it can't read anything or act outside mobile_app), so — like the other local
/// preference files — a plain JSON file is an acceptable risk tier; it doesn't
/// warrant the OS credential store the way the OAuth refresh token does.
/// </summary>
public static class MobileAppRegistrationStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaDesktop", "mobile-app.json");

    public static async Task SaveAsync(MobileAppRegistration registration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(registration));
    }

    public static async Task<MobileAppRegistration?> LoadAsync()
    {
        if (!File.Exists(FilePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(FilePath);
            return JsonSerializer.Deserialize<MobileAppRegistration>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Task ClearAsync()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
        return Task.CompletedTask;
    }
}
