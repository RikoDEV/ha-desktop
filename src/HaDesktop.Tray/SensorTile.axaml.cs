using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace HaDesktop.Tray;

/// <summary>Read-only display tile for a sensor entity (temperature, humidity, etc.) — no toggle, since there's nothing to actuate.</summary>
public partial class SensorTile : UserControl
{
    public string? EntityId { get; set; }

    public SensorTile()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <param name="iconKey">A key into <see cref="TileIcons.Paths"/>, not a display glyph.</param>
    public void SetContent(string iconKey, string label, string value)
    {
        this.FindControl<PathIcon>("IconIcon")!.Data = Geometry.Parse(TileIcons.PathFor(iconKey));
        this.FindControl<TextBlock>("ValueText")!.Text = value;
        this.FindControl<TextBlock>("LabelText")!.Text = label;
    }

    public void SetCornerRadius(double radius) =>
        this.FindControl<Border>("RootBorder")!.CornerRadius = new Avalonia.CornerRadius(radius);

    public void SetWide(bool wide) => Width = wide ? 184 : 88;
}
