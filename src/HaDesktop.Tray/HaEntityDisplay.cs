using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia.Media;
using HaDesktop.Core.Ha;

namespace HaDesktop.Tray;

/// <summary>Shared icon-key/label/value lookups used by the flyout, entity picker, and settings tile list.</summary>
public static class HaEntityDisplay
{
    /// <summary>Returns a key into <see cref="TileIcons.Paths"/>, not a display glyph.</summary>
    public static string IconFor(HaEntityState state) => state.Domain switch
    {
        "light" => "light",
        "switch" => "switch",
        "cover" => IconForCover(state),
        "sensor" => IconForSensor(state),
        "camera" => "camera",
        _ => "circle",
    };

    private static string IconForSensor(HaEntityState state)
    {
        var deviceClass = state.Attributes.TryGetValue("device_class", out var dc) && dc is string s ? s : "";
        return deviceClass switch
        {
            "temperature" => "thermometer",
            "humidity" => "humidity",
            _ => "circle",
        };
    }

    /// <summary>cover.* device_class (garage/door/gate/blind/shade/curtain/shutter/awning/window) to a more specific icon than the generic "cover" one.</summary>
    private static string IconForCover(HaEntityState state)
    {
        var deviceClass = state.Attributes.TryGetValue("device_class", out var dc) && dc is string s ? s : "";
        return deviceClass switch
        {
            "garage" => "garage",
            "door" or "gate" => "door",
            "blind" or "shade" or "curtain" or "shutter" or "awning" => "blinds",
            "window" => "window",
            _ => "cover",
        };
    }

    /// <summary>
    /// A light's current color from its rgb_color attribute, if it's on and reports one.
    /// HaClient serializes nested JSON (arrays/objects) as a raw JSON string rather than a
    /// structured value, so this parses "[255,180,90]" rather than reading a typed array.
    /// </summary>
    public static Color? LightColorFor(HaEntityState state)
    {
        if (!state.IsOn || !state.Attributes.TryGetValue("rgb_color", out var raw) || raw is not string json)
            return null;

        try
        {
            var array = JsonNode.Parse(json)?.AsArray();
            if (array is null || array.Count < 3) return null;

            var r = (byte)Math.Clamp(array[0]!.GetValue<double>(), 0, 255);
            var g = (byte)Math.Clamp(array[1]!.GetValue<double>(), 0, 255);
            var b = (byte)Math.Clamp(array[2]!.GetValue<double>(), 0, 255);
            return Color.FromRgb(r, g, b);
        }
        catch
        {
            return null; // malformed/unexpected attribute shape — fall back to the theme's default tile color
        }
    }

    public static string LabelFor(HaEntityState state) =>
        state.Attributes.TryGetValue("friendly_name", out var name) && name is string s ? s : state.EntityId;

    /// <summary>State + unit_of_measurement, for read-only sensor tiles (e.g. "21.5 °C").</summary>
    public static string ValueFor(HaEntityState state)
    {
        var unit = state.Attributes.TryGetValue("unit_of_measurement", out var u) && u is string us ? us : "";
        return string.IsNullOrEmpty(unit) ? state.State : $"{state.State} {unit}";
    }

    /// <summary>Maps a weather.* entity's condition state (e.g. "partlycloudy") to a <see cref="TileIcons.Paths"/> key.</summary>
    public static string WeatherIconFor(HaEntityState state) => WeatherIconForCondition(state.State);

    /// <summary>Same mapping as <see cref="WeatherIconFor(HaEntityState)"/>, for a forecast day's raw condition string.</summary>
    public static string WeatherIconForCondition(string? condition) => condition switch
    {
        "sunny" or "clear-night" => "circle",
        "cloudy" or "partlycloudy" or "fog" or "hazy" => "cloud",
        "rainy" or "pouring" or "snowy" or "snowy-rainy" or "hail" => "humidity",
        "lightning" or "lightning-rainy" => "storm",
        "windy" or "windy-variant" => "fan",
        _ => "circle",
    };

    /// <summary>"partlycloudy" -> "Partly Cloudy".</summary>
    public static string PrettifyCondition(string condition)
    {
        var words = condition.Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..]));
    }

    /// <summary>Current temperature + unit from a weather.* entity's attributes (e.g. "21.5°C").</summary>
    public static string WeatherTemperatureFor(HaEntityState state)
    {
        if (!state.Attributes.TryGetValue("temperature", out var temp) || temp is null)
            return "—";

        var unit = state.Attributes.TryGetValue("temperature_unit", out var u) && u is string us ? us : "°";
        return $"{temp}{unit}";
    }

    /// <summary>Top/bottom colors for a condition-tinted gradient background (e.g. blue sky for "sunny", dark navy for "clear-night").</summary>
    public static (Color Top, Color Bottom) WeatherGradientFor(string? condition) => condition switch
    {
        "sunny" => (Color.Parse("#4FC3F7"), Color.Parse("#0288D1")),
        "clear-night" => (Color.Parse("#283593"), Color.Parse("#0D1333")),
        "cloudy" or "partlycloudy" or "fog" or "hazy" => (Color.Parse("#90A4AE"), Color.Parse("#455A64")),
        "rainy" or "pouring" => (Color.Parse("#607D8B"), Color.Parse("#263238")),
        "snowy" or "snowy-rainy" or "hail" => (Color.Parse("#CFD8DC"), Color.Parse("#78909C")),
        "lightning" or "lightning-rainy" => (Color.Parse("#5E35B1"), Color.Parse("#1A0033")),
        "windy" or "windy-variant" => (Color.Parse("#78909C"), Color.Parse("#37474F")),
        _ => (Color.Parse("#78909C"), Color.Parse("#455A64")),
    };
}
