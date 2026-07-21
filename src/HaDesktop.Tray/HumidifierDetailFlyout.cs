using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HaDesktop.Core.Ha;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

/// <summary>
/// Right-click detail popup for a humidifier tile, mirroring Home Assistant's own humidifier
/// dashboard card: target humidity stepper, current reading, and a mode row (if the entity
/// supports one). On/off itself stays on the tile's own left-click toggle, same as light tiles
/// leave power on the tile and put brightness/color in their own detail popup.
/// </summary>
public static class HumidifierDetailFlyout
{
    public static void Show(Control anchor, string entityId, HaEntityState state, HaClient client)
    {
        var content = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(12), Width = 220 };

        if (HaEntityDisplay.NumberAttribute(state, "current_humidity") is { } current)
            content.Children.Add(new TextBlock { Text = Loc.Instance.Tr("Humidifier.CurrentHumidity", current), FontSize = 12, Opacity = 0.7 });

        if (HaEntityDisplay.NumberAttribute(state, "humidity") is { } target)
        {
            var min = HaEntityDisplay.NumberAttribute(state, "min_humidity") ?? 0;
            var max = HaEntityDisplay.NumberAttribute(state, "max_humidity") ?? 100;
            content.Children.Add(BuildStepper(Loc.Instance.Tr("Humidifier.TargetHumidity"), target, min, max,
                v => CallAsync(client, "humidifier", "set_humidity", entityId, new JsonObject { ["humidity"] = v })));
        }

        var modes = HaEntityDisplay.StringListAttribute(state, "available_modes");
        if (modes.Length > 0)
        {
            var currentMode = state.Attributes.TryGetValue("mode", out var m) && m is string ms ? ms : null;
            content.Children.Add(new TextBlock { Text = Loc.Instance.Tr("Humidifier.Mode"), FontSize = 12, Opacity = 0.7, Margin = new Avalonia.Thickness(0, 4, 0, 0) });

            var panel = new WrapPanel();
            foreach (var mode in modes)
            {
                var button = new Button { Content = HaEntityDisplay.Prettify(mode), Margin = new Avalonia.Thickness(0, 0, 4, 4), Padding = new Avalonia.Thickness(8, 4) };
                if (mode == currentMode) button.Classes.Add("accent");

                button.Click += async (_, _) =>
                {
                    foreach (var child in panel.Children)
                        if (child is Button b) b.Classes.Remove("accent");
                    button.Classes.Add("accent");
                    await CallAsync(client, "humidifier", "set_mode", entityId, new JsonObject { ["mode"] = mode });
                };

                panel.Children.Add(button);
            }

            content.Children.Add(panel);
        }

        var flyout = new Flyout { Content = content, Placement = PlacementMode.Bottom };
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }

    /// <summary>A -/label+value/+ row; changes debounce 250ms before the service call, same as the light brightness slider.</summary>
    private static Control BuildStepper(string label, double initial, double min, double max, Func<double, Task> onChanged)
    {
        var value = initial;
        var valueText = new TextBlock { Text = $"{value:0}%", FontSize = 15, FontWeight = FontWeight.SemiBold, Width = 56, TextAlignment = Avalonia.Media.TextAlignment.Center };

        DispatcherTimer? debounce = null;
        void ScheduleUpdate()
        {
            valueText.Text = $"{value:0}%";
            debounce?.Stop();
            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            debounce.Tick += async (_, _) =>
            {
                debounce!.Stop();
                try { await onChanged(value); }
                catch { /* best effort — tile resyncs from the next state_changed event */ }
            };
            debounce.Start();
        }

        var minusButton = new Button { Content = new PathIcon { Data = Geometry.Parse(TileIcons.PathFor("minus")), Width = 14, Height = 14 }, Width = 32, Height = 32, Padding = new Avalonia.Thickness(0) };
        minusButton.Click += (_, _) => { value = Math.Max(min, value - 5); ScheduleUpdate(); };

        var plusButton = new Button { Content = new PathIcon { Data = Geometry.Parse(TileIcons.PathFor("plus")), Width = 14, Height = 14 }, Width = 32, Height = 32, Padding = new Avalonia.Thickness(0) };
        plusButton.Click += (_, _) => { value = Math.Min(max, value + 5); ScheduleUpdate(); };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(minusButton);
        row.Children.Add(valueText);
        row.Children.Add(plusButton);

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 });
        panel.Children.Add(row);
        return panel;
    }

    private static async Task CallAsync(HaClient client, string domain, string service, string entityId, JsonObject data)
    {
        try { await client.CallServiceAsync(domain, service, entityId, data); }
        catch { /* best effort — tile resyncs from the next state_changed event */ }
    }
}
