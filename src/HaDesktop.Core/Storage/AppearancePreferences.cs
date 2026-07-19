namespace HaDesktop.Core.Storage;

public enum TileShape { Rounded, Square, Pill }

public sealed record AppearancePreferences(TileShape Shape)
{
    public static AppearancePreferences Default { get; } = new(TileShape.Rounded);

    public double TileCornerRadius => Shape switch
    {
        TileShape.Square => 0,
        TileShape.Pill => 38, // half the 76px tile height, for a true stadium shape
        _ => 6,
    };
}
