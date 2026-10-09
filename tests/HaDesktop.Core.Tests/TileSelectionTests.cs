using HaDesktop.Core.Storage;

namespace HaDesktop.Core.Tests;

public class TileSelectionTests
{
    private static TileConfig Group(params string[] members) =>
        new("group:g1", Size: TileSize.Group, Row: 0, Col: 0, GroupEntityIds: members.ToList());

    [Fact]
    public void SelectedEntityIds_ListsGroupMembersInsteadOfTheGroupId()
    {
        var tiles = new List<TileConfig> { new("light.a"), Group("switch.b", "switch.c") };

        Assert.Equal(new[] { "light.a", "switch.b", "switch.c" }, TileSelection.SelectedEntityIds(tiles).Order());
    }

    [Fact]
    public void Apply_KeepsExistingTilesWithTheirCustomizationsAndOrder()
    {
        var tiles = new List<TileConfig>
        {
            new("light.a", CustomLabel: "Desk", Size: TileSize.Wide),
            new("switch.b", CustomColor: "#FF0000"),
        };

        var result = TileSelection.Apply(tiles, new[] { "switch.b", "light.a" });

        Assert.Equal(tiles, result);
    }

    [Fact]
    public void Apply_KeepsAGroupWhoseMembersAreAllStillChosen()
    {
        var group = Group("switch.b", "switch.c");
        var tiles = new List<TileConfig> { new("light.a"), group };

        var result = TileSelection.Apply(tiles, new[] { "light.a", "switch.b", "switch.c" });

        Assert.Equal(2, result.Count);
        Assert.Equal("group:g1", result[1].EntityId);
        Assert.Equal(new[] { "switch.b", "switch.c" }, result[1].GroupEntityIds);
    }

    [Fact]
    public void Apply_DropsOnlyTheUncheckedMemberOfAGroup()
    {
        var tiles = new List<TileConfig> { Group("switch.b", "switch.c", "switch.d") };

        var result = TileSelection.Apply(tiles, new[] { "switch.b", "switch.d" });

        var group = Assert.Single(result);
        Assert.Equal(TileSize.Group, group.Size);
        Assert.Equal(new[] { "switch.b", "switch.d" }, group.GroupEntityIds);
    }

    [Fact]
    public void Apply_TurnsAGroupWithOneMemberLeftBackIntoAnOrdinaryTile()
    {
        var tiles = new List<TileConfig> { Group("switch.b", "switch.c") };

        var result = TileSelection.Apply(tiles, new[] { "switch.c" });

        var tile = Assert.Single(result);
        Assert.Equal("switch.c", tile.EntityId);
        Assert.Equal(TileSize.Small, tile.Size);
        Assert.Null(tile.GroupEntityIds);
    }

    [Fact]
    public void Apply_RemovesAGroupWithNoMembersLeftAndUncheckedTiles()
    {
        var tiles = new List<TileConfig> { new("light.a"), Group("switch.b", "switch.c") };

        Assert.Empty(TileSelection.Apply(tiles, Array.Empty<string>()));
    }

    [Fact]
    public void Apply_AppendsNewlyChosenEntitiesWithoutDuplicatingGroupMembers()
    {
        var tiles = new List<TileConfig> { Group("switch.b", "switch.c") };

        var result = TileSelection.Apply(tiles, new[] { "cover.new", "switch.b", "switch.c", "light.new" });

        Assert.Equal(new[] { "group:g1", "cover.new", "light.new" }, result.Select(t => t.EntityId));
    }
}
