using System;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HaDesktop.Core.Ha;

namespace HaDesktop.Tray;

/// <summary>Right-click detail popup for a light tile: brightness slider + a handful of preset color swatches.</summary>
public static class LightDetailFlyout
{
    private static readonly (string Name, byte R, byte G, byte B)[] Swatches =
    {
        ("Red", 255, 0, 0),
        ("Orange", 255, 140, 0),
        ("Yellow", 255, 214, 0),
        ("Green", 0, 200, 83),
        ("Blue", 41, 121, 255),
        ("Purple", 170, 0, 255),
        ("Warm White", 255, 214, 170),
        ("Cool White", 255, 255, 255),
    };

    public static void Show(Control anchor, string entityId, HaEntityState state, HaClient client)
    {
        var initialPercent = state.Attributes.TryGetValue("brightness", out var b) && b is double brightness
            ? (int)Math.Round(brightness / 255.0 * 100)
            : 100;

        var brightnessLabel = new TextBlock { Text = $"Brightness: {initialPercent}%", FontSize = 12 };
        var slider = new Slider { Minimum = 1, Maximum = 100, Value = initialPercent, Width = 200 };

        DispatcherTimer? debounce = null;
        slider.ValueChanged += (_, _) =>
        {
            var percent = (int)slider.Value;
            brightnessLabel.Text = $"Brightness: {percent}%";

            debounce?.Stop();
            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            debounce.Tick += async (_, _) =>
            {
                debounce!.Stop();
                try
                {
                    await client.CallServiceAsync("light", "turn_on", entityId,
                        new JsonObject { ["brightness_pct"] = percent });
                }
                catch { /* best effort */ }
            };
            debounce.Start();
        };

        var swatchPanel = new WrapPanel { Margin = new Avalonia.Thickness(0, 8, 0, 0), MaxWidth = 200 };
        foreach (var (name, r, g, bl) in Swatches)
        {
            var swatch = new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Avalonia.Thickness(3),
                CornerRadius = new Avalonia.CornerRadius(12),
                Background = new SolidColorBrush(Color.FromRgb(r, g, bl)),
                BorderThickness = new Avalonia.Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            };
            ToolTip.SetTip(swatch, name);
            swatch.Click += async (_, _) =>
            {
                try
                {
                    await client.CallServiceAsync("light", "turn_on", entityId,
                        new JsonObject { ["rgb_color"] = new JsonArray(r, g, bl) });
                }
                catch { /* best effort */ }
            };
            swatchPanel.Children.Add(swatch);
        }

        var colorWheelLabel = new TextBlock { Text = "Custom Color", FontSize = 12, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        var colorWheel = new ColorSpectrum
        {
            Width = 200,
            Height = 200,
            Shape = ColorSpectrumShape.Ring,
            Color = HaEntityDisplay.LightColorFor(state) ?? Colors.White,
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
        };

        DispatcherTimer? colorDebounce = null;
        colorWheel.ColorChanged += (_, _) =>
        {
            var picked = colorWheel.Color;

            colorDebounce?.Stop();
            colorDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            colorDebounce.Tick += async (_, _) =>
            {
                colorDebounce!.Stop();
                try
                {
                    await client.CallServiceAsync("light", "turn_on", entityId,
                        new JsonObject { ["rgb_color"] = new JsonArray(picked.R, picked.G, picked.B) });
                }
                catch { /* best effort */ }
            };
            colorDebounce.Start();
        };

        var content = new StackPanel
        {
            Spacing = 4,
            Margin = new Avalonia.Thickness(12),
        };
        content.Children.Add(brightnessLabel);
        content.Children.Add(slider);
        content.Children.Add(swatchPanel);
        content.Children.Add(colorWheelLabel);
        content.Children.Add(colorWheel);

        var flyout = new Flyout { Content = content, Placement = PlacementMode.Bottom };
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }
}
