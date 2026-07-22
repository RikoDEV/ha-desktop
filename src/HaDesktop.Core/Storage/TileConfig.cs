using System;
using System.Collections.Generic;

namespace HaDesktop.Core.Storage;

public enum TileSize { Small, Wide, Tall, Large, Group }

/// <summary>
/// A chosen quick-toggle tile, with optional overrides for the entity's default icon/friendly
/// name/size, and its position in the flyout's fixed-column grid. Row/Col of -1 means "not yet
/// positioned" — <see cref="TileLayoutCompactor"/> assigns a real position the next time the
/// tile list is loaded or saved, so callers that don't know about the grid (EntityPickerWindow
/// adding a newly-checked entity, or an old tiles.json predating positions entirely) never need
/// to compute one themselves.
///
/// A Group tile (Size == Group) has no real HA entity of its own — <see cref="EntityId"/> is a
/// synthetic "group:" id, and <see cref="GroupEntityIds"/> holds the up to 4 real entity ids
/// rendered as mini-quadrants inside that one 2x2 grid slot.
/// </summary>
public sealed record TileConfig(
    string EntityId,
    string? CustomLabel = null,
    string? CustomIcon = null,
    TileSize Size = TileSize.Small,
    int Row = -1,
    int Col = -1,
    List<string>? GroupEntityIds = null,
    bool IsGauge = false,
    string? CustomColor = null)
{
    public static TileConfig NewGroup(string firstEntityId, string secondEntityId, int row, int col) => new(
        "group:" + Guid.NewGuid().ToString("N"),
        Size: TileSize.Group,
        Row: row,
        Col: col,
        GroupEntityIds: new List<string> { firstEntityId, secondEntityId });
}
