namespace HaDesktop.Core.Ha;

/// <summary>One day's forecast from weather.get_forecasts (type: daily) — a separate API from entity state, not an attribute.</summary>
public sealed record HaForecastEntry(
    DateTimeOffset? DateTime,
    string? Condition,
    double? Temperature,
    double? TempLow,
    double? Humidity,
    double? WindSpeed);
