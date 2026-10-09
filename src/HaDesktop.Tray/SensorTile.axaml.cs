using Avalonia.Controls;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>Read-only display tile for a sensor entity (temperature, humidity, etc.) — no toggle, since there's nothing to actuate.</summary>
public partial class SensorTile : UserControl, IEntityTile
{
    private TileConfig? _config;

    public SensorTile()
    {
        InitializeComponent();
    }

    public void Configure(TileConfig config, double cornerRadius)
    {
        _config = config;
        RootBorder.CornerRadius = new Avalonia.CornerRadius(cornerRadius);
        if (TileDimensions.CustomBrushFor(config) is { } brush) RootBorder.Background = brush;
        this.SetTileSize(config.Size);
    }

    public void Update(HaEntityState state)
    {
        IconIcon.Data = TileIcons.GeometryFor(_config?.CustomIcon ?? HaEntityDisplay.IconFor(state));
        ValueText.Text = HaEntityDisplay.ValueFor(state);
        LabelText.Text = _config?.CustomLabel ?? HaEntityDisplay.LabelFor(state);
    }
}
