using System.Collections.Generic;
using System.Linq;

namespace HaDesktop.Core.Storage;

/// <summary>
/// Assigns a grid Row/Col to any <see cref="TileConfig"/> that doesn't have one yet (legacy tiles
/// from before the grid model, or freshly added ones from EntityPickerWindow), packing them
/// left-to-right/top-to-bottom in list order into the first cell(s) their size fits, without
/// disturbing tiles that already have an explicit position.
/// </summary>
public static class TileLayoutCompactor
{
    public const int ColumnCount = 3;

    public static int ColSpanFor(TileSize size) => size is TileSize.Wide or TileSize.Group ? 2 : 1;
    public static int RowSpanFor(TileSize size) => size == TileSize.Group ? 2 : 1;

    public static bool Overlaps(int row, int col, int colSpan, int rowSpan, TileConfig other)
    {
        var otherColSpan = ColSpanFor(other.Size);
        var otherRowSpan = RowSpanFor(other.Size);
        var overlapsRows = row < other.Row + otherRowSpan && other.Row < row + rowSpan;
        var overlapsCols = col < other.Col + otherColSpan && other.Col < col + colSpan;
        return overlapsRows && overlapsCols;
    }

    /// <summary>
    /// Fully re-packs every tile in list order, with no gaps — a tile's position is always purely
    /// derived from where it sits in the list, never preserved from a prior Row/Col. That's what
    /// makes plain drag-to-reorder work: the layout editor's job when reordering a tile is only
    /// ever to move it to a new spot in the underlying list (AppSettings.MoveTileToIndexAsync),
    /// and this then lays the whole grid out fresh from that order — no separate "shift positions"
    /// logic needed, and no risk of a resize/merge leaving a hole or overlap behind.
    /// </summary>
    public static List<TileConfig> Defragment(IReadOnlyList<TileConfig> tiles) =>
        Compact(tiles.Select(t => t with { Row = -1, Col = -1 }).ToList());

    public static List<TileConfig> Compact(IReadOnlyList<TileConfig> tiles)
    {
        var occupied = new HashSet<(int Row, int Col)>();
        foreach (var tile in tiles)
        {
            if (tile.Row < 0 || tile.Col < 0) continue;
            MarkOccupied(occupied, tile.Row, tile.Col, ColSpanFor(tile.Size), RowSpanFor(tile.Size));
        }

        var result = new List<TileConfig>(tiles.Count);
        foreach (var tile in tiles)
        {
            if (tile.Row >= 0 && tile.Col >= 0)
            {
                result.Add(tile);
                continue;
            }

            var colSpan = ColSpanFor(tile.Size);
            var rowSpan = RowSpanFor(tile.Size);
            var (row, col) = FindFreeCell(occupied, colSpan, rowSpan);
            MarkOccupied(occupied, row, col, colSpan, rowSpan);
            result.Add(tile with { Row = row, Col = col });
        }

        return result;
    }

    private static (int Row, int Col) FindFreeCell(HashSet<(int Row, int Col)> occupied, int colSpan, int rowSpan)
    {
        for (var row = 0; ; row++)
        {
            for (var col = 0; col <= ColumnCount - colSpan; col++)
            {
                if (Fits(occupied, row, col, colSpan, rowSpan))
                    return (row, col);
            }
        }
    }

    private static bool Fits(HashSet<(int Row, int Col)> occupied, int row, int col, int colSpan, int rowSpan)
    {
        for (var r = row; r < row + rowSpan; r++)
            for (var c = col; c < col + colSpan; c++)
                if (occupied.Contains((r, c)))
                    return false;
        return true;
    }

    private static void MarkOccupied(HashSet<(int Row, int Col)> occupied, int row, int col, int colSpan, int rowSpan)
    {
        for (var r = row; r < row + rowSpan; r++)
            for (var c = col; c < col + colSpan; c++)
                occupied.Add((r, c));
    }
}
