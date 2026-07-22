using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>
/// Read-only half-circle gauge for a numeric sensor, matching Home Assistant's own gauge card:
/// a muted background track plus a colored value arc (green/yellow/red by severity), with the
/// current value centered underneath.
/// </summary>
public partial class GaugeTile : UserControl
{
    private const double CenterX = 32;
    private const double CenterY = 30;
    private const double Radius = 26;

    public string? EntityId { get; set; }

    public GaugeTile()
    {
        InitializeComponent();
        this.FindControl<Path>("TrackPath")!.Data = ArcGeometry(0, 1);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void SetContent(HaEntityState state, string label)
    {
        this.FindControl<TextBlock>("LabelText")!.Text = label;

        var fraction = HaEntityDisplay.GaugeFractionFor(state);
        var valuePath = this.FindControl<Path>("ValuePath")!;

        if (fraction is not { } f)
        {
            valuePath.Data = null;
            this.FindControl<TextBlock>("ValueText")!.Text = "—";
            return;
        }

        valuePath.Data = ArcGeometry(0, Math.Max(f, 0.001)); // a sliver even at 0 so the arc's rounded cap is visible
        valuePath.Stroke = new SolidColorBrush(HaEntityDisplay.GaugeColorFor(f));
        this.FindControl<TextBlock>("ValueText")!.Text = HaEntityDisplay.ValueFor(state);
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

    public void SetCornerRadius(double radius) =>
        this.FindControl<Border>("RootBorder")!.CornerRadius = new CornerRadius(radius);

    /// <summary>Overrides the tile's background with a user-picked color; a fresh tile instance already shows the theme default otherwise (see FlyoutWindow — tiles are rebuilt from scratch on every refresh), so this only ever needs to act when a color is actually set.</summary>
    public void SetCustomColor(Color? color)
    {
        if (color is { } c) this.FindControl<Border>("RootBorder")!.Background = new SolidColorBrush(c);
    }

    public void SetSize(TileSize size)
    {
        Width = TileDimensions.WidthFor(size);
        Height = TileDimensions.HeightFor(size);
    }
}
