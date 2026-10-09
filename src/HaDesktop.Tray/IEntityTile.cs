using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>
/// A flyout tile bound to one Home Assistant entity (or, for a group tile, up to four). The flyout
/// creates a tile once, configures it, and from then on only pushes state into it — so a tile acts
/// on its own entity through <see cref="HaActions"/> rather than through handlers wired up from outside.
/// </summary>
public interface IEntityTile
{
    /// <summary>Applies the user's choices for this tile (size, color, label/icon overrides) and the app-wide corner radius. Called once, before the first <see cref="Update"/>.</summary>
    void Configure(TileConfig config, double cornerRadius);

    /// <summary>Shows the entity's current state.</summary>
    void Update(HaEntityState state);
}
