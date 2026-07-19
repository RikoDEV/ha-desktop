namespace HaDesktop.Core.Storage;

/// <summary>
/// EntityId null means "auto" — the flyout picks the best media_player entity itself
/// (currently playing, else paused, else the first available) instead of requiring setup.
/// </summary>
public sealed record MediaPlayerPreferences(bool Enabled, string? EntityId, bool UseAlbumArtBackground = true)
{
    public static MediaPlayerPreferences Default { get; } = new(true, null, true);
}
