using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>
/// One 2x2 grid slot showing up to 4 entities as mini icon+state quadrants — tapping a quadrant
/// performs that entity's quick action (toggle for light/switch, open/close cycle for cover).
/// Analogous to a Windows Start Menu folder tile.
/// </summary>
public partial class GroupTile : UserControl
{
    /// <summary>The synthetic "group:" TileConfig.EntityId this tile renders, not a real HA entity.</summary>
    public string? GroupId { get; set; }

    public event EventHandler<string>? QuadrantActionRequested;

    public GroupTile()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void SetCornerRadius(double radius) =>
        this.FindControl<Border>("RootBorder")!.CornerRadius = new CornerRadius(radius);

    public void SetSize(TileSize size)
    {
        // Group tiles are always 184x160 (2x2) — kept only for API symmetry with the other tile controls.
    }

    public void SetQuadrants(IReadOnlyList<(string EntityId, HaEntityState State)> entities)
    {
        var grid = this.FindControl<Grid>("QuadrantGrid")!;
        grid.Children.Clear();

        for (var i = 0; i < 4; i++)
        {
            var cell = i < entities.Count
                ? BuildQuadrant(entities[i].EntityId, entities[i].State)
                : new Border { Background = Brushes.Transparent };
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            grid.Children.Add(cell);
        }
    }

    private Control BuildQuadrant(string entityId, HaEntityState state)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = state.IsOn
                ? new SolidColorBrush(Color.Parse("#3D5C9EFF"))
                : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
        };
        stack.Children.Add(new PathIcon
        {
            Data = Geometry.Parse(TileIcons.PathFor(HaEntityDisplay.IconFor(state))),
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = HaEntityDisplay.LabelFor(state),
            FontSize = 9,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 80,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        border.Child = stack;

        border.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
            QuadrantActionRequested?.Invoke(this, entityId);
        };

        return border;
    }
}
