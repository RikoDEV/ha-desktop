using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace HaDesktop.Tray;

/// <summary>Popup for renaming a tile and/or picking a custom icon, opened from the tile list in Settings.</summary>
public static class TileEditFlyout
{
    public static void Show(Control anchor, string? currentLabel, string? currentIconKey, string defaultLabel, string defaultIconKey, Func<string?, string?, Task> onSave)
    {
        var labelBox = new TextBox { Watermark = defaultLabel, Text = currentLabel, Width = 232 };

        string? selectedIconKey = currentIconKey;
        Button? selectedButton = null;
        var iconButtons = new List<(string Key, Button Button)>();

        var iconRow = new WrapPanel { Margin = new Avalonia.Thickness(0, 4, 0, 4), MaxWidth = 232 };
        foreach (var (key, path) in TileIcons.Paths)
        {
            var iconButton = new Button
            {
                Content = new PathIcon { Data = Geometry.Parse(path), Width = 18, Height = 18 },
                Width = 36,
                Height = 36,
                Margin = new Avalonia.Thickness(2),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            if (key == selectedIconKey)
            {
                iconButton.Classes.Add("accent");
                selectedButton = iconButton;
            }

            iconButton.Click += (_, _) =>
            {
                selectedButton?.Classes.Remove("accent");
                iconButton.Classes.Add("accent");
                selectedButton = iconButton;
                selectedIconKey = key;
            };

            iconButtons.Add((key, iconButton));
            iconRow.Children.Add(iconButton);
        }

        Flyout? flyout = null;

        var saveButton = new Button { Content = "Save", Classes = { "accent" } };
        saveButton.Click += async (_, _) =>
        {
            var label = string.IsNullOrWhiteSpace(labelBox.Text) ? null : labelBox.Text.Trim();
            await onSave(label, selectedIconKey);
            flyout?.Hide();
        };

        var resetButton = new Button { Content = "Reset to Default" };
        resetButton.Click += async (_, _) =>
        {
            await onSave(null, null);
            flyout?.Hide();
        };

        var content = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(12), Width = 256 };
        content.Children.Add(new TextBlock { Text = "Custom Name", FontSize = 12, Opacity = 0.7 });
        content.Children.Add(labelBox);
        content.Children.Add(new TextBlock { Text = "Icon", FontSize = 12, Opacity = 0.7, Margin = new Avalonia.Thickness(0, 4, 0, 0) });
        content.Children.Add(iconRow);
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
            Children = { resetButton, saveButton },
        });

        // The icon grid can grow taller than the available screen space near the
        // anchor (e.g. a tile row near the bottom of the Settings window) — without
        // a scroll container the flyout has no way to reach content past the edge.
        var scroller = new ScrollViewer
        {
            Content = content,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        flyout = new Flyout { Content = scroller, Placement = PlacementMode.Bottom };
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }
}
