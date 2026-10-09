namespace HaDesktop.Core.Storage;

/// <summary>Reconciles the tile list with a plain "which entities are ticked" answer from the entity picker.</summary>
public static class TileSelection
{
    /// <summary>Every real entity the tiles show — a group tile contributes its members, not its own synthetic id.</summary>
    public static HashSet<string> SelectedEntityIds(IEnumerable<TileConfig> tiles)
    {
        var ids = new HashSet<string>();
        foreach (var tile in tiles)
        {
            if (tile.GroupEntityIds is { } members) ids.UnionWith(members);
            else ids.Add(tile.EntityId);
        }

        return ids;
    }

    /// <summary>
    /// The tile list after the picker is saved with <paramref name="chosenEntityIds"/> ticked.
    /// Existing tiles keep their place and customizations. A group keeps the members still ticked:
    /// it survives with two or more, turns back into an ordinary tile with one, and goes away with
    /// none. Newly ticked entities are appended, in the order given.
    /// </summary>
    public static List<TileConfig> Apply(IReadOnlyList<TileConfig> existing, IReadOnlyList<string> chosenEntityIds)
    {
        var chosen = new HashSet<string>(chosenEntityIds);
        var kept = new HashSet<string>();
        var result = new List<TileConfig>();

        foreach (var tile in existing)
        {
            if (tile.GroupEntityIds is { } members)
            {
                var remaining = members.Where(chosen.Contains).ToList();
                if (remaining.Count == 0) continue;

                result.Add(remaining.Count == 1
                    ? new TileConfig(remaining[0], Row: tile.Row, Col: tile.Col)
                    : tile with { GroupEntityIds = remaining });
                kept.UnionWith(remaining);
            }
            else if (chosen.Contains(tile.EntityId))
            {
                result.Add(tile);
                kept.Add(tile.EntityId);
            }
        }

        foreach (var entityId in chosenEntityIds)
            if (kept.Add(entityId)) result.Add(new TileConfig(entityId));

        return result;
    }
}
