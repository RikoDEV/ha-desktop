using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HaDesktop.Core.Storage;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

/// <summary>The user's preferences for this session, loaded from and saved to <see cref="PreferenceStores"/>. The Home Assistant connection itself lives in <see cref="HaSession"/>.</summary>
public static class AppSettings
{
    public static List<TileConfig> SelectedTiles { get; private set; } = new();
    public static SensorPreferences SensorPrefs { get; private set; } = SensorPreferences.Default;
    public static AppearancePreferences Appearance { get; private set; } = AppearancePreferences.Default;
    public static WeatherPreferences WeatherPrefs { get; private set; } = WeatherPreferences.Default;
    public static MediaPlayerPreferences MediaPlayerPrefs { get; private set; } = MediaPlayerPreferences.Default;
    public static FlyoutWindowPreferences FlyoutWindowPrefs { get; private set; } = FlyoutWindowPreferences.Default;
    public static bool NotificationsEnabled { get; private set; } = true;
    public static bool UpdateCheckEnabled { get; private set; } = true;
    public static AppLanguage Language => Loc.Instance.Current;

    /// <summary>Raised whenever a new client becomes connected (or reconnected after a token refresh) or the tile selection/customization changes.</summary>
    public static event Action? ConnectionChanged;

    /// <summary>
    /// Raised once <see cref="LoadLocalPreferencesAsync"/> finishes. FlyoutWindow is constructed
    /// (and its own constructor already run) before that load kicks off — see App.axaml.cs — so it
    /// can't just read <see cref="FlyoutWindowPrefs"/> at construction time; it applies the saved
    /// size from this event instead, the same async-then-notify pattern <see cref="ConnectionChanged"/>
    /// already uses for tile selection.
    /// </summary>
    public static event Action? LocalPreferencesLoaded;

    internal static void RaiseConnectionChanged() => ConnectionChanged?.Invoke();

    public static async Task LoadLocalPreferencesAsync()
    {
        var loadedTiles = await PreferenceStores.Tiles.LoadAsync();
        SelectedTiles = TileLayoutCompactor.Defragment(loadedTiles);
        // Persist immediately if defragmenting actually changed anything, so a legacy tiles.json
        // (unpositioned entirely, or with gaps left over from an older version) is normalized
        // once rather than being recomputed — harmlessly, but pointlessly — on every load.
        if (loadedTiles.Count > 0)
            await PreferenceStores.Tiles.SaveAsync(SelectedTiles);
        SensorPrefs = await PreferenceStores.Sensors.LoadAsync();
        Appearance = await PreferenceStores.Appearance.LoadAsync();
        WeatherPrefs = await PreferenceStores.Weather.LoadAsync();
        MediaPlayerPrefs = await PreferenceStores.MediaPlayer.LoadAsync();
        FlyoutWindowPrefs = await PreferenceStores.FlyoutWindow.LoadAsync();
        NotificationsEnabled = await PreferenceStores.Notifications.LoadAsync();
        UpdateCheckEnabled = await PreferenceStores.UpdateCheck.LoadAsync();
        await HaSession.LoadRegistrationAsync();

        var languagePrefs = await PreferenceStores.Language.LoadAsync();
        Loc.Instance.SetLanguage(languagePrefs.Language);

        LocalPreferencesLoaded?.Invoke();
    }

    /// <summary>
    /// Which entities the app needs live state for: every selected tile (and each member of a
    /// group tile), the weather widget's entity, and the media widget's — every media_player when
    /// that one is left on "auto". With no tiles selected yet the flyout falls back to showing
    /// lights/switches/covers, so those domains are tracked whole until a selection exists.
    /// </summary>
    internal static Func<string, bool> BuildStateFilter()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tile in SelectedTiles)
        {
            if (tile.GroupEntityIds is { } members) ids.UnionWith(members);
            else ids.Add(tile.EntityId);
        }

        if (WeatherPrefs is { Enabled: true, EntityId: { } weatherId }) ids.Add(weatherId);
        if (MediaPlayerPrefs is { Enabled: true, EntityId: { } mediaPlayerId }) ids.Add(mediaPlayerId);

        var anyMediaPlayer = MediaPlayerPrefs is { Enabled: true, EntityId: null };
        var defaultTileDomains = SelectedTiles.Count == 0;

        return entityId =>
            ids.Contains(entityId)
            || (anyMediaPlayer && entityId.StartsWith("media_player.", StringComparison.Ordinal))
            || (defaultTileDomains && IsDefaultTileDomain(entityId));
    }

    internal static bool IsDefaultTileDomain(string entityId) =>
        entityId.StartsWith("light.", StringComparison.Ordinal)
        || entityId.StartsWith("switch.", StringComparison.Ordinal)
        || entityId.StartsWith("cover.", StringComparison.Ordinal);

    /// <summary>Debounced by the caller (FlyoutWindow) while the user is actively dragging a resize handle, so this isn't hit on every intermediate pixel.</summary>
    public static async Task SetFlyoutWindowSizeAsync(double width, double height)
    {
        FlyoutWindowPrefs = new FlyoutWindowPreferences(width, height);
        await PreferenceStores.FlyoutWindow.SaveAsync(FlyoutWindowPrefs);
    }

    public static async Task SetLanguageAsync(AppLanguage language)
    {
        Loc.Instance.SetLanguage(language);
        await PreferenceStores.Language.SaveAsync(new LanguagePreferences(language));
    }

    public static async Task SetNotificationsEnabledAsync(bool enabled)
    {
        NotificationsEnabled = enabled;
        await PreferenceStores.Notifications.SaveAsync(enabled);
    }

    public static async Task SetUpdateCheckEnabledAsync(bool enabled)
    {
        UpdateCheckEnabled = enabled;
        await PreferenceStores.UpdateCheck.SaveAsync(enabled);
    }

    public static async Task SetMediaPlayerPreferencesAsync(MediaPlayerPreferences prefs)
    {
        var tracksDifferentEntities = (prefs.Enabled, prefs.EntityId) != (MediaPlayerPrefs.Enabled, MediaPlayerPrefs.EntityId);
        MediaPlayerPrefs = prefs;
        await PreferenceStores.MediaPlayer.SaveAsync(prefs);
        if (tracksDifferentEntities) await HaSession.RefreshTrackedStatesAsync();
        ConnectionChanged?.Invoke();
    }

    public static async Task SetWeatherPreferencesAsync(WeatherPreferences prefs)
    {
        var tracksDifferentEntities = (prefs.Enabled, prefs.EntityId) != (WeatherPrefs.Enabled, WeatherPrefs.EntityId);
        WeatherPrefs = prefs;
        await PreferenceStores.Weather.SaveAsync(prefs);
        if (tracksDifferentEntities) await HaSession.RefreshTrackedStatesAsync();
        ConnectionChanged?.Invoke();
    }

    public static async Task SetSelectedTilesAsync(List<TileConfig> tiles)
    {
        var previousEntities = TrackedTileEntities(SelectedTiles);

        // Defragmented here, not just on load, so every mutation path (EntityPickerWindow adding
        // a bare new TileConfig, a resize/group-merge that leaves a hole or an overlap, removing
        // a tile, etc.) always ends up gap-free without each call site needing grid awareness.
        SelectedTiles = TileLayoutCompactor.Defragment(tiles);
        await PreferenceStores.Tiles.SaveAsync(SelectedTiles);

        // Most edits (reorder, resize, rename, recolor) leave the set of entities untouched —
        // only an added or removed one needs the client to re-read states.
        if (!previousEntities.SetEquals(TrackedTileEntities(SelectedTiles)))
            await HaSession.RefreshTrackedStatesAsync();
        ConnectionChanged?.Invoke();
    }

    private static HashSet<string> TrackedTileEntities(List<TileConfig> tiles) =>
        tiles.SelectMany(t => t.GroupEntityIds ?? (IEnumerable<string>)new[] { t.EntityId }).ToHashSet();

    public static async Task UpdateTileAsync(string entityId, string? customLabel, string? customIcon, bool isGauge = false, string? customColor = null)
    {
        var updated = SelectedTiles
            .Select(t => t.EntityId == entityId ? t with { CustomLabel = customLabel, CustomIcon = customIcon, IsGauge = isGauge, CustomColor = customColor } : t)
            .ToList();
        await SetSelectedTilesAsync(updated);
    }

    public static async Task SetTileSizeAsync(string entityId, TileSize size)
    {
        // Growing a tile (Small -> Wide) can run it off the grid's right edge or straight into a
        // neighbor's cell — SetSelectedTilesAsync's Defragment always fully re-packs the grid
        // gap-free, so it resolves that (and any hole left behind by shrinking) automatically.
        var updated = SelectedTiles.Select(t => t.EntityId == entityId ? t with { Size = size } : t).ToList();
        await SetSelectedTilesAsync(updated);
    }

    /// <summary>
    /// Moves a tile to a new spot in list order — since Defragment always derives every tile's grid
    /// position purely from where it sits in this list, this is the one operation the layout editor's
    /// drag-to-reorder ultimately reduces to (whether dropped in empty space or next to another tile).
    /// </summary>
    public static async Task MoveTileToIndexAsync(string entityId, int index)
    {
        var moved = SelectedTiles.FirstOrDefault(t => t.EntityId == entityId);
        if (moved is null) return;

        var others = SelectedTiles.Where(t => t.EntityId != entityId).ToList();
        others.Insert(Math.Clamp(index, 0, others.Count), moved);
        await SetSelectedTilesAsync(others);
    }

    /// <summary>Merges two small tiles into a new 2x2 Group tile at the target's former position — dragging one small tile onto another.</summary>
    public static async Task CreateGroupAsync(string targetEntityId, string draggedEntityId)
    {
        var target = SelectedTiles.FirstOrDefault(t => t.EntityId == targetEntityId);
        if (target is null) return;

        var group = TileConfig.NewGroup(targetEntityId, draggedEntityId, target.Row, target.Col);
        var updated = SelectedTiles
            .Where(t => t.EntityId != targetEntityId && t.EntityId != draggedEntityId)
            .Append(group)
            .ToList();
        await SetSelectedTilesAsync(updated);
    }

    /// <summary>Adds one more entity into an existing Group's quadrants (up to 4) — dragging a small tile onto a Group with room left.</summary>
    public static async Task AddToGroupAsync(string groupId, string entityId)
    {
        var group = SelectedTiles.FirstOrDefault(t => t.EntityId == groupId);
        if (group?.GroupEntityIds is not { Count: < 4 } members) return;

        var updated = SelectedTiles
            .Where(t => t.EntityId != entityId)
            .Select(t => t.EntityId == groupId ? t with { GroupEntityIds = new List<string>(members) { entityId } } : t)
            .ToList();
        await SetSelectedTilesAsync(updated);
    }

    /// <summary>
    /// Removes one entity from a Group back onto the main grid as its own Small tile, in the group's
    /// former list slot — the layout editor's "drag a tile out of a group" gesture calls this first,
    /// then repositions the freshly-extracted tile whereever it was actually dropped. Dissolves the
    /// Group entirely (converting the sole remaining entity back to a bare Small TileConfig) once
    /// only one member is left, or removes the Group outright if it was the last member.
    /// </summary>
    public static async Task RemoveFromGroupAsync(string groupId, string entityId)
    {
        var group = SelectedTiles.FirstOrDefault(t => t.EntityId == groupId);
        if (group?.GroupEntityIds is null) return;

        var remaining = group.GroupEntityIds.Where(id => id != entityId).ToList();
        var updated = SelectedTiles
            .SelectMany(t =>
            {
                if (t.EntityId != groupId) return new[] { t };
                return remaining.Count switch
                {
                    0 => Array.Empty<TileConfig>(),
                    1 => new[] { new TileConfig(remaining[0]) },
                    _ => new[] { group with { GroupEntityIds = remaining } },
                };
            })
            .Append(new TileConfig(entityId)) // unpositioned — repositioned right after by the caller
            .ToList();

        await SetSelectedTilesAsync(updated);
    }

    public static async Task SetAppearanceAsync(AppearancePreferences appearance)
    {
        Appearance = appearance;
        await PreferenceStores.Appearance.SaveAsync(appearance);
        ConnectionChanged?.Invoke();
    }

    public static async Task SetSensorPreferencesAsync(SensorPreferences prefs)
    {
        var deviceRenamed = prefs.DeviceName != SensorPrefs.DeviceName;
        SensorPrefs = prefs;
        await PreferenceStores.Sensors.SaveAsync(prefs);

        if (deviceRenamed) await HaSession.RenameDeviceAsync(prefs.DeviceName);
        SensorPublisher.Update();
    }
}
