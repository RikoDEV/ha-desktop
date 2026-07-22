namespace HaDesktop.Core.Storage;

public sealed record FlyoutWindowPreferences(double Width, double Height)
{
    public static FlyoutWindowPreferences Default { get; } = new(320, 470);
}
