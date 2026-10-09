using System.Text.Json.Nodes;
using Avalonia.Controls;
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
    public static void Show(Control anchor, HaEntityState state)
    {
        var entityId = state.EntityId;
        var content = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(12), Width = 220 };

        if (HaEntityDisplay.NumberAttribute(state, "current_humidity") is { } current)
            content.Children.Add(DetailFlyoutControls.BuildSectionLabel(Loc.Instance.Tr("Humidifier.CurrentHumidity", current), spaced: false));

        if (HaEntityDisplay.NumberAttribute(state, "humidity") is { } target)
        {
            var min = HaEntityDisplay.NumberAttribute(state, "min_humidity") ?? 0;
            var max = HaEntityDisplay.NumberAttribute(state, "max_humidity") ?? 100;
            content.Children.Add(DetailFlyoutControls.BuildStepper(Loc.Instance.Tr("Humidifier.TargetHumidity"), target, min, max, step: 5, snapToStep: false,
                value => $"{value:0}%",
                value => HaActions.CallAsync("humidifier", "set_humidity", entityId, new JsonObject { ["humidity"] = value })));
        }

        var modes = HaEntityDisplay.StringListAttribute(state, "available_modes");
        if (modes.Length > 0)
        {
            var currentMode = state.Attributes.TryGetValue("mode", out var m) && m is string ms ? ms : null;
            content.Children.Add(DetailFlyoutControls.BuildSectionLabel(Loc.Instance.Tr("Humidifier.Mode")));
            content.Children.Add(DetailFlyoutControls.BuildModeRow(modes, currentMode, HaEntityDisplay.Prettify,
                mode => HaActions.CallAsync("humidifier", "set_mode", entityId, new JsonObject { ["mode"] = mode })));
        }

        DetailFlyoutControls.Show(anchor, content);
    }
}
