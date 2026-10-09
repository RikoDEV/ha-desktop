using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>
/// One 2x2 grid slot showing up to 4 entities as mini icon+state quadrants — tapping a quadrant
/// performs that entity's quick action (toggle for light/switch, open/close cycle for cover).
/// Analogous to a Windows Start Menu folder tile. Always 184x160; its TileConfig's size is ignored.
/// </summary>
public partial class GroupTile : UserControl, IEntityTile
{
    private static readonly IBrush OnBrush = new SolidColorBrush(Color.Parse("#3D5C9EFF"));

    private IReadOnlyList<string> _memberIds = Array.Empty<string>();

    public GroupTile()
    {
        InitializeComponent();
    }

    public void Configure(TileConfig config, double cornerRadius)
    {
        _memberIds = config.GroupEntityIds ?? (IReadOnlyList<string>)Array.Empty<string>();
        RootBorder.CornerRadius = new CornerRadius(cornerRadius);
    }

    /// <summary>One member changed — the quadrants are cheap enough to all be redrawn from the session's current states.</summary>
    public void Update(HaEntityState state)
    {
        var states = HaSession.Client?.States;

        // Quadrants fill in order, skipping members HA no longer knows about, so a vanished entity
        // doesn't leave a hole in the middle of the tile.
        var present = new List<HaEntityState>(_memberIds.Count);
        foreach (var id in _memberIds)
        {
            if (id == state.EntityId) present.Add(state);
            else if (states is not null && states.TryGetValue(id, out var member)) present.Add(member);
        }

        QuadrantGrid.Children.Clear();
        for (var i = 0; i < 4; i++)
        {
            var cell = i < present.Count ? BuildQuadrant(present[i]) : new Border { Background = Brushes.Transparent };
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            QuadrantGrid.Children.Add(cell);
        }
    }

    private static Control BuildQuadrant(HaEntityState state)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = state.IsOn ? OnBrush : Brushes.Transparent,
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
            Data = TileIcons.GeometryFor(HaEntityDisplay.IconFor(state)),
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
            if (e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
                _ = HaActions.QuickActionAsync(state);
        };

        return border;
    }
}
