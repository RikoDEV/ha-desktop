using System.Text.Json.Serialization;

namespace HaDesktop.Core.Storage;

/// <summary>Every user preference this app persists, one file each.</summary>
public static class PreferenceStores
{
    /// <summary>Which entities the user has chosen to show as quick-toggle tiles, and any label/icon overrides.</summary>
    public static JsonFileStore<List<TileConfig>> Tiles { get; } =
        new("tiles.json", StorageJsonContext.Default.ListTileConfig, () => new List<TileConfig>());

    /// <summary>Which local sensors the user opted in to sharing with Home Assistant, and under what device name.</summary>
    public static JsonFileStore<SensorPreferences> Sensors { get; } =
        new("sensor-preferences.json", StorageJsonContext.Default.SensorPreferences, () => SensorPreferences.Default);

    public static JsonFileStore<AppearancePreferences> Appearance { get; } =
        new("appearance.json", StorageJsonContext.Default.AppearancePreferences, () => AppearancePreferences.Default);

    public static JsonFileStore<WeatherPreferences> Weather { get; } =
        new("weather.json", StorageJsonContext.Default.WeatherPreferences, () => WeatherPreferences.Default);

    public static JsonFileStore<MediaPlayerPreferences> MediaPlayer { get; } =
        new("media-player.json", StorageJsonContext.Default.MediaPlayerPreferences, () => MediaPlayerPreferences.Default);

    public static JsonFileStore<FlyoutWindowPreferences> FlyoutWindow { get; } =
        new("window.json", StorageJsonContext.Default.FlyoutWindowPreferences, () => FlyoutWindowPreferences.Default);

    public static JsonFileStore<LanguagePreferences> Language { get; } =
        new("language.json", StorageJsonContext.Default.LanguagePreferences, () => LanguagePreferences.Default);

    /// <summary>Whether to display incoming Home Assistant push notifications as native OS notifications.</summary>
    public static DisabledFlagStore Notifications { get; } = new("notifications-disabled.flag");

    /// <summary>Whether to check GitHub Releases for a newer app version.</summary>
    public static DisabledFlagStore UpdateCheck { get; } = new("update-check-disabled.flag");
}

/// <summary>Source-generated serialization for everything persisted here, so none of it depends on runtime reflection (which trimming strips).</summary>
[JsonSerializable(typeof(List<TileConfig>))]
[JsonSerializable(typeof(SensorPreferences))]
[JsonSerializable(typeof(AppearancePreferences))]
[JsonSerializable(typeof(WeatherPreferences))]
[JsonSerializable(typeof(MediaPlayerPreferences))]
[JsonSerializable(typeof(FlyoutWindowPreferences))]
[JsonSerializable(typeof(LanguagePreferences))]
[JsonSerializable(typeof(PersistedHaCredentials))]
[JsonSerializable(typeof(MobileAppRegistrationStore.PersistedMetadata))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
