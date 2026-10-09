using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using HaDesktop.Core.Ha;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

/// <summary>Right-click detail popup for a light tile: brightness slider + a handful of preset color swatches.</summary>
public static class LightDetailFlyout
{
    private static readonly (string NameKey, byte R, byte G, byte B)[] Swatches =
    {
        ("Light.ColorRed", 255, 0, 0),
        ("Light.ColorOrange", 255, 140, 0),
        ("Light.ColorYellow", 255, 214, 0),
        ("Light.ColorGreen", 0, 200, 83),
        ("Light.ColorBlue", 41, 121, 255),
        ("Light.ColorPurple", 170, 0, 255),
        ("Light.ColorWarmWhite", 255, 214, 170),
        ("Light.ColorCoolWhite", 255, 255, 255),
    };

    public static void Show(Control anchor, HaEntityState state)
    {
        var entityId = state.EntityId;

        // A light that's off reports no brightness at all — show full, which is what switching it
        // on from here with a brightness value would give anyway.
        var initialPercent = HaEntityDisplay.NumberAttribute(state, "brightness") is { } brightness
            ? (int)Math.Round(brightness / 255.0 * 100)
            : 100;

        var brightnessLabel = new TextBlock { Text = Loc.Instance.Tr("Light.Brightness", initialPercent), FontSize = 12 };
        var slider = new Slider { Minimum = 1, Maximum = 100, Value = initialPercent, Width = 200 };

        var brightnessDebouncer = new Debouncer();
        slider.ValueChanged += (_, _) =>
        {
            var percent = (int)slider.Value;
            brightnessLabel.Text = Loc.Instance.Tr("Light.Brightness", percent);
            brightnessDebouncer.Schedule(() => HaActions.CallAsync("light", "turn_on", entityId, new JsonObject { ["brightness_pct"] = percent }));
        };

        var swatchPanel = new WrapPanel { Margin = new Avalonia.Thickness(0, 8, 0, 0), MaxWidth = 200 };
        foreach (var (nameKey, r, g, b) in Swatches)
        {
            var swatch = new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Avalonia.Thickness(3),
                CornerRadius = new Avalonia.CornerRadius(12),
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                BorderThickness = new Avalonia.Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            };
            ToolTip.SetTip(swatch, Loc.Instance.Tr(nameKey));
            swatch.Click += (_, _) => _ = SetColorAsync(Color.FromRgb(r, g, b));
            swatchPanel.Children.Add(swatch);
        }

        var colorWheelLabel = new TextBlock { Text = Loc.Instance.Tr("Light.CustomColor"), FontSize = 12, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        var colorWheel = new ColorSpectrum
        {
            Width = 200,
            Height = 200,
            Shape = ColorSpectrumShape.Ring,
            Color = HaEntityDisplay.LightColorFor(state) ?? Colors.White,
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
        };

        var colorDebouncer = new Debouncer();
        colorWheel.ColorChanged += (_, _) =>
        {
            var picked = colorWheel.Color;
            colorDebouncer.Schedule(() => SetColorAsync(picked));
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

        DetailFlyoutControls.Show(anchor, content);

        Task SetColorAsync(Color color) =>
            HaActions.CallAsync("light", "turn_on", entityId, new JsonObject { ["rgb_color"] = new JsonArray(color.R, color.G, color.B) });
    }
}
