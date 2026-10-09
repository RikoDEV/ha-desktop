using HaDesktop.Core.Storage;

namespace HaDesktop.Core.Tests;

public class TileLayoutCompactorTests
{
    private static (string Id, int Row, int Col)[] Positions(IEnumerable<TileConfig> tiles) =>
        tiles.Select(t => (t.EntityId, t.Row, t.Col)).ToArray();

    [Fact]
    public void Compact_PacksUnpositionedSmallTilesInReadingOrder()
    {
        var tiles = new[] { "a", "b", "c", "d" }.Select(id => new TileConfig(id)).ToList();

        var result = TileLayoutCompactor.Compact(tiles);

        Assert.Equal(new[] { ("a", 0, 0), ("b", 0, 1), ("c", 0, 2), ("d", 1, 0) }, Positions(result));
    }

    [Fact]
    public void Compact_LeavesPositionedTilesAloneAndFlowsAroundThem()
    {
        var tiles = new List<TileConfig>
        {
            new("pinned", Row: 0, Col: 1),
            new("a"),
            new("b"),
        };

        var result = TileLayoutCompactor.Compact(tiles);

        Assert.Equal(new[] { ("pinned", 0, 1), ("a", 0, 0), ("b", 0, 2) }, Positions(result));
    }

    [Fact]
    public void Compact_WideTileThatDoesNotFitTheRowMovesToTheNext()
    {
        var tiles = new List<TileConfig>
        {
            new("a"),
            new("b"),
            new("wide", Size: TileSize.Wide),
            new("c"),
        };

        var result = TileLayoutCompactor.Compact(tiles);

        // The wide tile needs two free columns, so it starts row 1; "c" then backfills the hole left in row 0.
        Assert.Equal(new[] { ("a", 0, 0), ("b", 0, 1), ("wide", 1, 0), ("c", 0, 2) }, Positions(result));
    }

    [Fact]
    public void Compact_TallAndLargeTilesReserveBothRows()
    {
        var tiles = new List<TileConfig>
        {
            new("large", Size: TileSize.Large),
            new("tall", Size: TileSize.Tall),
            new("a"),
        };

        var result = TileLayoutCompactor.Compact(tiles);

        Assert.Equal(new[] { ("large", 0, 0), ("tall", 0, 2), ("a", 2, 0) }, Positions(result));
    }

    [Fact]
    public void Defragment_IgnoresStoredPositions()
    {
        var tiles = new List<TileConfig>
        {
            new("a", Row: 5, Col: 2),
            new("b", Row: 0, Col: 0),
        };

        var result = TileLayoutCompactor.Defragment(tiles);

        Assert.Equal(new[] { ("a", 0, 0), ("b", 0, 1) }, Positions(result));
    }

    [Fact]
    public void Defragment_ReflowsIntoTheGivenColumnCount()
    {
        var tiles = new[] { "a", "b", "c", "d", "e" }.Select(id => new TileConfig(id)).ToList();

        var result = TileLayoutCompactor.Defragment(tiles, columnCount: 2);

        Assert.Equal(new[] { ("a", 0, 0), ("b", 0, 1), ("c", 1, 0), ("d", 1, 1), ("e", 2, 0) }, Positions(result));
    }

    [Fact]
    public void Compact_WideTileInASingleColumnIsClampedInsteadOfLoopingForever()
    {
        var tiles = new List<TileConfig> { new("wide", Size: TileSize.Wide), new("a") };

        var result = TileLayoutCompactor.Compact(tiles, columnCount: 1);

        Assert.Equal(new[] { ("wide", 0, 0), ("a", 1, 0) }, Positions(result));
    }

    [Theory]
    [InlineData(TileSize.Small, 1, 1)]
    [InlineData(TileSize.Wide, 2, 1)]
    [InlineData(TileSize.Tall, 1, 2)]
    [InlineData(TileSize.Large, 2, 2)]
    [InlineData(TileSize.Group, 2, 2)]
    public void Spans_MatchEachTileSize(TileSize size, int colSpan, int rowSpan)
    {
        Assert.Equal(colSpan, TileLayoutCompactor.ColSpanFor(size));
        Assert.Equal(rowSpan, TileLayoutCompactor.RowSpanFor(size));
    }

    [Fact]
    public void Overlaps_DetectsSharedCellsButNotNeighbours()
    {
        var large = new TileConfig("large", Size: TileSize.Large, Row: 1, Col: 1);

        Assert.True(TileLayoutCompactor.Overlaps(row: 2, col: 2, colSpan: 1, rowSpan: 1, large));
        Assert.True(TileLayoutCompactor.Overlaps(row: 0, col: 0, colSpan: 2, rowSpan: 2, large));
        Assert.False(TileLayoutCompactor.Overlaps(row: 1, col: 0, colSpan: 1, rowSpan: 1, large));
        Assert.False(TileLayoutCompactor.Overlaps(row: 3, col: 1, colSpan: 2, rowSpan: 1, large));
    }
}
