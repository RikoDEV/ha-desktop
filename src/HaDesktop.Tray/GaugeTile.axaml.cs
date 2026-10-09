using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>
/// Read-only half-circle gauge for a numeric sensor, matching Home Assistant's own gauge card:
/// a muted background track plus a colored value arc (green/yellow/red by severity), with the
/// current value centered underneath.
/// </summary>
public partial class GaugeTile : UserControl, IEntityTile
{
    private const double CenterX = 32;
    private const double CenterY = 30;
    private const double Radius = 26;

    private TileConfig? _config;

    public GaugeTile()
    {
        InitializeComponent();
        TrackPath.Data = ArcGeometry(0, 1);
    }

    public void Configure(TileConfig config, double cornerRadius)
    {
        _config = config;
        RootBorder.CornerRadius = new CornerRadius(cornerRadius);
        if (TileDimensions.CustomBrushFor(config) is { } brush) RootBorder.Background = brush;
        this.SetTileSize(config.Size);
    }

    public void Update(HaEntityState state)
    {
        LabelText.Text = _config?.CustomLabel ?? HaEntityDisplay.LabelFor(state);

        if (HaEntityDisplay.GaugeFractionFor(state) is not { } fraction)
        {
            ValuePath.Data = null;
            ValueText.Text = "—";
            return;
        }

        ValuePath.Data = ArcGeometry(0, Math.Max(fraction, 0.001)); // a sliver even at 0 so the arc's rounded cap is visible
        ValuePath.Stroke = HaEntityDisplay.GaugeBrushFor(fraction);
        ValueText.Text = HaEntityDisplay.ValueFor(state);
    }

    /// <summary>
    /// Builds a semicircle arc from fraction <paramref name="fromFraction"/> to <paramref name="toFraction"/>
    /// (0 = left end, 1 = right end, sweeping clockwise over the top) as a stream geometry.
    /// </summary>
    private static StreamGeometry ArcGeometry(double fromFraction, double toFraction)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(PointOnArc(fromFraction), isFilled: false);
        ctx.ArcTo(PointOnArc(toFraction), new Size(Radius, Radius), 0, isLargeArc: false, SweepDirection.Clockwise);
        return geometry;
    }

    /// <summary>0 = left end of the semicircle (180°), 1 = right end (0°), sweeping over the top.</summary>
    private static Point PointOnArc(double fraction)
    {
        var angle = Math.PI * (1 - fraction); // 180° at fraction 0, 0° at fraction 1
        return new Point(CenterX + Radius * Math.Cos(angle), CenterY - Radius * Math.Sin(angle));
    }
}
