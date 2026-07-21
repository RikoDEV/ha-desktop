using System;
using System.Collections.Generic;
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
/// Right-click detail popup for a climate tile, mirroring Home Assistant's own thermostat dashboard
/// card: target temperature stepper(s) (a low/high pair in heat_cool/range mode, a single setpoint
/// otherwise), the current reading, and hvac-mode/preset-mode buttons.
/// </summary>
public static class ThermostatDetailFlyout
{
    public static void Show(Control anchor, string entityId, HaEntityState state, HaClient client)
    {
        var content = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(12), Width = 232 };

        if (HaEntityDisplay.NumberAttribute(state, "current_temperature") is { } current)
            content.Children.Add(new TextBlock { Text = Loc.Instance.Tr("Climate.CurrentTemp", current), FontSize = 12, Opacity = 0.7 });

        var minTemp = HaEntityDisplay.NumberAttribute(state, "min_temp") ?? 7;
        var maxTemp = HaEntityDisplay.NumberAttribute(state, "max_temp") ?? 35;
        var step = HaEntityDisplay.NumberAttribute(state, "target_temp_step") ?? 0.5;

        if (HaEntityDisplay.NumberAttribute(state, "target_temp_low") is { } low && HaEntityDisplay.NumberAttribute(state, "target_temp_high") is { } high)
        {
            content.Children.Add(BuildStepper(Loc.Instance.Tr("Climate.TargetLow"), low, minTemp, maxTemp, step,
                v => CallAsync(client, "climate", "set_temperature", entityId, new JsonObject { ["target_temp_low"] = v, ["target_temp_high"] = high })));
            content.Children.Add(BuildStepper(Loc.Instance.Tr("Climate.TargetHigh"), high, minTemp, maxTemp, step,
                v => CallAsync(client, "climate", "set_temperature", entityId, new JsonObject { ["target_temp_low"] = low, ["target_temp_high"] = v })));
        }
        else if (HaEntityDisplay.NumberAttribute(state, "temperature") is { } target)
        {
            content.Children.Add(BuildStepper(Loc.Instance.Tr("Climate.TargetTemp"), target, minTemp, maxTemp, step,
                v => CallAsync(client, "climate", "set_temperature", entityId, new JsonObject { ["temperature"] = v })));
        }

        var hvacModes = HaEntityDisplay.StringListAttribute(state, "hvac_modes");
        if (hvacModes.Length > 0)
        {
            content.Children.Add(new TextBlock { Text = Loc.Instance.Tr("Climate.Mode"), FontSize = 12, Opacity = 0.7, Margin = new Avalonia.Thickness(0, 4, 0, 0) });
            content.Children.Add(BuildModeRow(hvacModes, state.State, HaEntityDisplay.PrettifyHvacMode,
                mode => CallAsync(client, "climate", "set_hvac_mode", entityId, new JsonObject { ["hvac_mode"] = mode })));
        }

        var presetModes = HaEntityDisplay.StringListAttribute(state, "preset_modes");
        if (presetModes.Length > 0)
        {
            var currentPreset = state.Attributes.TryGetValue("preset_mode", out var pm) && pm is string pms ? pms : null;
            content.Children.Add(new TextBlock { Text = Loc.Instance.Tr("Climate.Preset"), FontSize = 12, Opacity = 0.7, Margin = new Avalonia.Thickness(0, 4, 0, 0) });
            content.Children.Add(BuildModeRow(presetModes, currentPreset, HaEntityDisplay.Prettify,
                mode => CallAsync(client, "climate", "set_preset_mode", entityId, new JsonObject { ["preset_mode"] = mode })));
        }

        var flyout = new Flyout { Content = content, Placement = PlacementMode.Bottom };
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }

    /// <summary>A -/label+value/+ row; changes debounce 250ms before the service call, same as the light brightness slider.</summary>
    private static Control BuildStepper(string label, double initial, double min, double max, double step, Func<double, Task> onChanged)
    {
        var value = initial;
        var valueText = new TextBlock { Text = $"{value:0.#}°", FontSize = 15, FontWeight = FontWeight.SemiBold, Width = 56, TextAlignment = Avalonia.Media.TextAlignment.Center };

        DispatcherTimer? debounce = null;
        void ScheduleUpdate()
        {
            valueText.Text = $"{value:0.#}°";
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
        minusButton.Click += (_, _) => { value = Math.Max(min, Math.Round((value - step) / step) * step); ScheduleUpdate(); };

        var plusButton = new Button { Content = new PathIcon { Data = Geometry.Parse(TileIcons.PathFor("plus")), Width = 14, Height = 14 }, Width = 32, Height = 32, Padding = new Avalonia.Thickness(0) };
        plusButton.Click += (_, _) => { value = Math.Min(max, Math.Round((value + step) / step) * step); ScheduleUpdate(); };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(minusButton);
        row.Children.Add(valueText);
        row.Children.Add(plusButton);

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 });
        panel.Children.Add(row);
        return panel;
    }

    /// <summary>A row of toggle-style buttons, one per mode, with the current one pre-highlighted and re-highlighted immediately on click (the flyout has no live state feed of its own).</summary>
    private static Control BuildModeRow(string[] modes, string? currentMode, Func<string, string> displayNameFor, Func<string, Task> onSelect)
    {
        var panel = new WrapPanel();
        var buttons = new List<(string Mode, Button Button)>();

        foreach (var mode in modes)
        {
            var button = new Button { Content = displayNameFor(mode), Margin = new Avalonia.Thickness(0, 0, 4, 4), Padding = new Avalonia.Thickness(8, 4) };
            if (mode == currentMode) button.Classes.Add("accent");

            button.Click += async (_, _) =>
            {
                foreach (var (_, b) in buttons) b.Classes.Remove("accent");
                button.Classes.Add("accent");
                try { await onSelect(mode); }
                catch { /* best effort */ }
            };

            buttons.Add((mode, button));
            panel.Children.Add(button);
        }

        return panel;
    }

    private static async Task CallAsync(HaClient client, string domain, string service, string entityId, JsonObject data)
    {
        try { await client.CallServiceAsync(domain, service, entityId, data); }
        catch { /* best effort — tile resyncs from the next state_changed event */ }
    }
}
