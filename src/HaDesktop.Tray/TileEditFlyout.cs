using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

/// <summary>Popup for renaming a tile and/or picking a custom icon, opened from the tile list in Settings.</summary>
public static class TileEditFlyout
{
    // Fixed palette rather than a full color picker (à la LightDetailFlyout's ColorSpectrum) —
    // this colors the whole card's background, not a light's actual bulb color, so a handful of
    // tasteful, readable-with-white-or-black-text swatches covers it without the extra weight of
    // a spectrum wheel for what's a cosmetic-only pick.
    private static (byte R, byte G, byte B)[] ColorSwatches => new (byte, byte, byte)[]
    {
        (244, 67, 54), (255, 152, 0), (255, 214, 0), (76, 175, 80),
        (0, 188, 212), (41, 121, 255), (156, 39, 176), (120, 144, 156),
    };

    public static void Show(Control anchor, string? currentLabel, string? currentIconKey, string defaultLabel, string defaultIconKey, bool isSensor, bool currentIsGauge, string? currentColor, Func<string?, string?, bool, string?, Task> onSave)
    {
        var labelBox = new TextBox { Watermark = defaultLabel, Text = currentLabel, Width = 232 };
        var gaugeCheckBox = new CheckBox { Content = Loc.Instance.Tr("TileEdit.DisplayAsGauge"), IsChecked = currentIsGauge, IsVisible = isSensor };

        string? selectedColor = currentColor;
        Button? selectedColorButton = null;
        var colorRow = new WrapPanel { Margin = new Avalonia.Thickness(0, 4, 0, 4), MaxWidth = 232 };
        foreach (var (r, g, b) in ColorSwatches)
        {
            var hex = $"#{r:X2}{g:X2}{b:X2}";
            var swatch = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Avalonia.Thickness(2),
                CornerRadius = new Avalonia.CornerRadius(14),
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                BorderThickness = new Avalonia.Thickness(hex == selectedColor ? 3 : 1),
                BorderBrush = hex == selectedColor ? Brushes.White : new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            };
            if (hex == selectedColor) selectedColorButton = swatch;

            swatch.Click += (_, _) =>
            {
                if (selectedColorButton == swatch)
                {
                    // Clicking the already-selected swatch deselects it — back to the tile's default color.
                    swatch.BorderThickness = new Avalonia.Thickness(1);
                    swatch.BorderBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0));
                    selectedColorButton = null;
                    selectedColor = null;
                    return;
                }

                if (selectedColorButton is not null)
                {
                    selectedColorButton.BorderThickness = new Avalonia.Thickness(1);
                    selectedColorButton.BorderBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0));
                }
                swatch.BorderThickness = new Avalonia.Thickness(3);
                swatch.BorderBrush = Brushes.White;
                selectedColorButton = swatch;
                selectedColor = hex;
            };

            colorRow.Children.Add(swatch);
        }

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

        var saveButton = new Button { Content = Loc.Instance.Tr("TileEdit.Save"), Classes = { "accent" } };
        saveButton.Click += async (_, _) =>
        {
            var label = string.IsNullOrWhiteSpace(labelBox.Text) ? null : labelBox.Text.Trim();
            await onSave(label, selectedIconKey, gaugeCheckBox.IsChecked ?? false, selectedColor);
            flyout?.Hide();
        };

        var resetButton = new Button { Content = Loc.Instance.Tr("TileEdit.ResetToDefault") };
        resetButton.Click += async (_, _) =>
        {
            await onSave(null, null, false, null);
            flyout?.Hide();
        };

        var content = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(12), Width = 256 };
        content.Children.Add(new TextBlock { Text = Loc.Instance.Tr("TileEdit.CustomName"), FontSize = 12, Opacity = 0.7 });
        content.Children.Add(labelBox);
        content.Children.Add(new TextBlock { Text = Loc.Instance.Tr("TileEdit.Icon"), FontSize = 12, Opacity = 0.7, Margin = new Avalonia.Thickness(0, 4, 0, 0) });
        content.Children.Add(iconRow);
        content.Children.Add(new TextBlock { Text = Loc.Instance.Tr("TileEdit.Color"), FontSize = 12, Opacity = 0.7, Margin = new Avalonia.Thickness(0, 4, 0, 0) });
        content.Children.Add(colorRow);
        content.Children.Add(gaugeCheckBox);
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
