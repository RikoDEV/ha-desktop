namespace HaDesktop.Core.Storage;

public sealed record WeatherPreferences(
    bool Enabled,
    string? EntityId,
    bool ShowWindAndHumidity = true,
    bool ShowForecast = true,
    int ForecastDays = 4,
    bool ShowConditionBackground = true)
{
    public static WeatherPreferences Default { get; } = new(false, null, true, true, 4, true);
}
