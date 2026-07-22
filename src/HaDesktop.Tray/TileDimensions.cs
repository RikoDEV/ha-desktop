using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>Pixel width/height for each TileSize, shared by every tile control's SetSize. 184/160 match GroupTile's fixed 2-span size exactly (2*88+8, 2*76+8), so a Wide/Tall/Large tile lines up with a Group tile occupying the same span.</summary>
public static class TileDimensions
{
    public static double WidthFor(TileSize size) => TileLayoutCompactor.ColSpanFor(size) == 2 ? 184 : 88;
    public static double HeightFor(TileSize size) => TileLayoutCompactor.RowSpanFor(size) == 2 ? 160 : 76;
}
