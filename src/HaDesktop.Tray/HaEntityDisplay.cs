using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using HaDesktop.Core.Ha;
using HaDesktop.Tray.Localization;

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
        "climate" => "thermostat",
        "fan" => "fan",
        "humidifier" => "humidity",
        "lawn_mower" => "lawn-mower",
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

    /// <summary>A light's current color from its rgb_color attribute, if it's on and reports one.</summary>
    public static Color? LightColorFor(HaEntityState state)
    {
        if (!state.IsOn || !state.Attributes.TryGetValue("rgb_color", out var raw) || raw is not double[] { Length: >= 3 } rgb)
            return null;

        return Color.FromRgb(ToByte(rgb[0]), ToByte(rgb[1]), ToByte(rgb[2]));

        static byte ToByte(double channel) => (byte)Math.Clamp(channel, 0, 255);
    }

    /// <summary>A numeric (double) attribute, or null if missing/non-numeric — HaClient parses JSON numbers straight into <see cref="double"/>, never a string.</summary>
    public static double? NumberAttribute(HaEntityState state, string key) =>
        state.Attributes.TryGetValue(key, out var v) && v is double d ? d : null;

    /// <summary>A string-array attribute (e.g. hvac_modes, preset_modes, available_modes), or an empty array if missing or not a list of strings.</summary>
    public static string[] StringListAttribute(HaEntityState state, string key) =>
        state.Attributes.TryGetValue(key, out var raw) && raw is string[] list ? list : Array.Empty<string>();

    /// <summary>Title-cases an unrecognized snake/kebab-case value (e.g. a preset_mode or fan_mode with no dedicated translation) into display text, e.g. "away" -> "Away".</summary>
    public static string Prettify(string value)
    {
        var words = value.Replace('_', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..]));
    }

    public static string LabelFor(HaEntityState state) =>
        state.Attributes.TryGetValue("friendly_name", out var name) && name is string s ? s : state.EntityId;

    /// <summary>State + unit_of_measurement, for read-only sensor tiles (e.g. "21.5 °C").</summary>
    public static string ValueFor(HaEntityState state)
    {
        var unit = state.Attributes.TryGetValue("unit_of_measurement", out var u) && u is string us ? us : "";
        return string.IsNullOrEmpty(unit) ? state.State : $"{state.State} {unit}";
    }

    private static readonly Dictionary<string, string> HvacModeKeys = new()
    {
        ["off"] = "Climate.Off",
        ["heat"] = "Climate.Heat",
        ["cool"] = "Climate.Cool",
        ["heat_cool"] = "Climate.HeatCool",
        ["auto"] = "Climate.Auto",
        ["dry"] = "Climate.Dry",
        ["fan_only"] = "Climate.FanOnly",
    };

    /// <summary>Translates a climate.* hvac_mode (e.g. "heat_cool") to display text, falling back to title-casing unknown values.</summary>
    public static string PrettifyHvacMode(string mode) =>
        HvacModeKeys.TryGetValue(mode, out var key) ? Loc.Instance.Tr(key) : Prettify(mode);

    /// <summary>A climate.* entity's target (or current, if no target is set) temperature + unit, e.g. "21.5°C".</summary>
    public static string ClimateTemperatureFor(HaEntityState state)
    {
        var temp = state.Attributes.TryGetValue("temperature", out var t) && t is not null ? t
            : state.Attributes.TryGetValue("current_temperature", out var c) ? c : null;
        if (temp is null) return "—";

        var unit = state.Attributes.TryGetValue("temperature_unit", out var u) && u is string us ? us : "°";
        return $"{temp}{unit}";
    }

    private static readonly Dictionary<string, string> LawnMowerStateKeys = new()
    {
        ["mowing"] = "LawnMower.StatusMowing",
        ["docked"] = "LawnMower.StatusDocked",
        ["paused"] = "LawnMower.StatusPaused",
        ["returning"] = "LawnMower.StatusReturning",
        ["error"] = "LawnMower.StatusError",
        ["unavailable"] = "LawnMower.StatusUnavailable",
    };

    /// <summary>Translates a lawn_mower.* state to display text, falling back to title-casing unknown values.</summary>
    public static string LawnMowerStatusFor(HaEntityState state)
    {
        if (LawnMowerStateKeys.TryGetValue(state.State, out var key))
            return Loc.Instance.Tr(key);

        return PrettifyCondition(state.State);
    }

    /// <summary>
    /// A gauge's 0-1 fill fraction for a sensor's current numeric state, or null if the state
    /// isn't numeric (e.g. "unavailable"). Home Assistant's own gauge card requires min/max to be
    /// set explicitly per-dashboard-card since sensors don't self-report a range; this app has no
    /// such per-tile config UI yet, so <see cref="GaugeRangeFor"/> guesses a sensible 0-100 range
    /// from the unit/device_class instead (percent-like sensors — battery, humidity, CPU load —
    /// cover the overwhelming majority of real-world gauge use).
    /// </summary>
    public static double? GaugeFractionFor(HaEntityState state)
    {
        if (!double.TryParse(state.State, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;

        return Math.Clamp(value / 100, 0, 1); // 0-100 range — see summary above
    }

    /// <summary>Green/yellow/red severity zones, matching Home Assistant's own gauge card defaults (green below 50%, yellow 50-80%, red above 80%).</summary>
    public static IBrush GaugeBrushFor(double fraction) => fraction switch
    {
        >= 0.8 => GaugeRedBrush,
        >= 0.5 => GaugeYellowBrush,
        _ => GaugeGreenBrush,
    };

    private static readonly IBrush GaugeRedBrush = new SolidColorBrush(Color.Parse("#DB4437"));
    private static readonly IBrush GaugeYellowBrush = new SolidColorBrush(Color.Parse("#F4B400"));
    private static readonly IBrush GaugeGreenBrush = new SolidColorBrush(Color.Parse("#0F9D58"));

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

    private static readonly Dictionary<string, string> ConditionKeys = new()
    {
        ["sunny"] = "Weather.Sunny",
        ["clear-night"] = "Weather.ClearNight",
        ["cloudy"] = "Weather.Cloudy",
        ["partlycloudy"] = "Weather.PartlyCloudy",
        ["fog"] = "Weather.Fog",
        ["hazy"] = "Weather.Hazy",
        ["rainy"] = "Weather.Rainy",
        ["pouring"] = "Weather.Pouring",
        ["snowy"] = "Weather.Snowy",
        ["snowy-rainy"] = "Weather.SnowyRainy",
        ["hail"] = "Weather.Hail",
        ["lightning"] = "Weather.Lightning",
        ["lightning-rainy"] = "Weather.LightningRainy",
        ["windy"] = "Weather.Windy",
        ["windy-variant"] = "Weather.WindyVariant",
        ["exceptional"] = "Weather.Exceptional",
    };

    /// <summary>Translates a weather.* condition (e.g. "partlycloudy") to display text, falling back to title-casing unknown values.</summary>
    public static string PrettifyCondition(string condition) =>
        ConditionKeys.TryGetValue(condition, out var key) ? Loc.Instance.Tr(key) : Prettify(condition);

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
