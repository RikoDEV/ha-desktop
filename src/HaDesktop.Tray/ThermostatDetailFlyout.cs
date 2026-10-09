using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
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
    public static void Show(Control anchor, HaEntityState state)
    {
        var entityId = state.EntityId;
        var content = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(12), Width = 232 };

        if (HaEntityDisplay.NumberAttribute(state, "current_temperature") is { } current)
            content.Children.Add(DetailFlyoutControls.BuildSectionLabel(Loc.Instance.Tr("Climate.CurrentTemp", current), spaced: false));

        var minTemp = HaEntityDisplay.NumberAttribute(state, "min_temp") ?? 7;
        var maxTemp = HaEntityDisplay.NumberAttribute(state, "max_temp") ?? 35;
        var step = HaEntityDisplay.NumberAttribute(state, "target_temp_step") ?? 0.5;

        Control TemperatureStepper(string labelKey, double initial, Func<double, JsonObject> serviceData) =>
            DetailFlyoutControls.BuildStepper(Loc.Instance.Tr(labelKey), initial, minTemp, maxTemp, step, snapToStep: true,
                value => $"{value:0.#}°",
                value => HaActions.CallAsync("climate", "set_temperature", entityId, serviceData(value)));

        if (HaEntityDisplay.NumberAttribute(state, "target_temp_low") is { } low && HaEntityDisplay.NumberAttribute(state, "target_temp_high") is { } high)
        {
            content.Children.Add(TemperatureStepper("Climate.TargetLow", low, v => new JsonObject { ["target_temp_low"] = v, ["target_temp_high"] = high }));
            content.Children.Add(TemperatureStepper("Climate.TargetHigh", high, v => new JsonObject { ["target_temp_low"] = low, ["target_temp_high"] = v }));
        }
        else if (HaEntityDisplay.NumberAttribute(state, "temperature") is { } target)
        {
            content.Children.Add(TemperatureStepper("Climate.TargetTemp", target, v => new JsonObject { ["temperature"] = v }));
        }

        void AddModeRow(string labelKey, string[] modes, string? currentMode, Func<string, string> displayNameFor, string service, string dataKey)
        {
            if (modes.Length == 0) return;

            content.Children.Add(DetailFlyoutControls.BuildSectionLabel(Loc.Instance.Tr(labelKey)));
            content.Children.Add(DetailFlyoutControls.BuildModeRow(modes, currentMode, displayNameFor,
                mode => HaActions.CallAsync("climate", service, entityId, new JsonObject { [dataKey] = mode })));
        }

        AddModeRow("Climate.Mode", HaEntityDisplay.StringListAttribute(state, "hvac_modes"), state.State,
            HaEntityDisplay.PrettifyHvacMode, "set_hvac_mode", "hvac_mode");

        var currentPreset = state.Attributes.TryGetValue("preset_mode", out var pm) && pm is string pms ? pms : null;
        AddModeRow("Climate.Preset", HaEntityDisplay.StringListAttribute(state, "preset_modes"), currentPreset,
            HaEntityDisplay.Prettify, "set_preset_mode", "preset_mode");

        DetailFlyoutControls.Show(anchor, content);
    }
}
