using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using HaDesktop.Core.Diagnostics;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>Current conditions + optional wind/humidity and multi-day forecast, sourced entirely from a Home Assistant weather.* entity — no external weather API calls.</summary>
public partial class WeatherWidget : UserControl
{
    private int _forecastToken;
    private bool _useWhiteForeground;

    public WeatherWidget()
    {
        InitializeComponent();
        WindIcon.Data = TileIcons.GeometryFor("fan");
        HumidityIcon.Data = TileIcons.GeometryFor("humidity");
    }

    public void SetContent(HaEntityState state, WeatherPreferences prefs)
    {
        ConditionIcon.Data = TileIcons.GeometryFor(HaEntityDisplay.WeatherIconFor(state));
        TempText.Text = HaEntityDisplay.WeatherTemperatureFor(state);
        ConditionText.Text = HaEntityDisplay.PrettifyCondition(state.State);

        var overlay = ConditionBackgroundOverlay;
        if (prefs.ShowConditionBackground)
        {
            var (top, bottom) = HaEntityDisplay.WeatherGradientFor(state.State);
            overlay.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
            };
            overlay.IsVisible = true;
        }
        else
        {
            overlay.IsVisible = false;
        }
        _useWhiteForeground = prefs.ShowConditionBackground;
        ApplyForeground(_useWhiteForeground ? Brushes.White : null);

        var windHumidityPanel = WindHumidityPanel;
        if (prefs.ShowWindAndHumidity)
        {
            var windSpeed = state.Attributes.TryGetValue("wind_speed", out var ws) && ws is not null ? Convert.ToDouble(ws) : (double?)null;
            var windUnit = state.Attributes.TryGetValue("wind_speed_unit", out var wu) && wu is string wus ? wus : "";
            var humidity = state.Attributes.TryGetValue("humidity", out var h) && h is not null ? Convert.ToDouble(h) : (double?)null;

            WindText.Text = windSpeed is { } w ? $"{w:0.#} {windUnit}".Trim() : "—";
            HumidityText.Text = humidity is { } hum ? $"{hum:0}%" : "—";
            windHumidityPanel.IsVisible = true;
        }
        else
        {
            windHumidityPanel.IsVisible = false;
        }

        var forecastGrid = ForecastGrid;
        if (prefs.ShowForecast && prefs.ForecastDays > 0)
        {
            forecastGrid.IsVisible = true;
            _ = LoadForecastAsync(state.EntityId, prefs.ForecastDays);
        }
        else
        {
            forecastGrid.IsVisible = false;
            forecastGrid.Children.Clear();
        }
    }

    private async System.Threading.Tasks.Task LoadForecastAsync(string entityId, int days)
    {
        if (HaSession.Client is not { } client) return;

        var myToken = ++_forecastToken;
        List<HaForecastEntry> forecast;
        try { forecast = await client.GetForecastAsync(entityId, "daily"); }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            return; // best effort — leave whatever forecast was already showing
        }

        if (myToken != _forecastToken) return; // superseded by a newer call/track change while we were awaiting

        var forecastGrid = ForecastGrid;
        forecastGrid.Children.Clear();
        forecastGrid.Columns = Math.Min(days, forecast.Count);

        foreach (var day in forecast.Take(days))
            forecastGrid.Children.Add(BuildForecastDay(day, _useWhiteForeground));
    }

    private static Control BuildForecastDay(HaForecastEntry day, bool useWhiteForeground)
    {
        var panel = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(2, 0),
        };

        var dayText = new TextBlock
        {
            Text = day.DateTime?.ToString("ddd", CultureInfo.CurrentCulture) ?? "—",
            FontSize = 10,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var icon = new PathIcon
        {
            Data = TileIcons.GeometryFor(HaEntityDisplay.WeatherIconForCondition(day.Condition)),
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var tempText = new TextBlock
        {
            Text = FormatHighLow(day.Temperature, day.TempLow),
            FontSize = 10,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // Leaving Foreground unset (rather than explicitly assigning null) lets these freshly
        // constructed controls fall back to the theme's default text color when white isn't wanted.
        if (useWhiteForeground)
        {
            dayText.Foreground = Brushes.White;
            icon.Foreground = Brushes.White;
            tempText.Foreground = Brushes.White;
        }

        panel.Children.Add(dayText);
        panel.Children.Add(icon);
        panel.Children.Add(tempText);
        return panel;
    }

    private static string FormatHighLow(double? high, double? low) => (high, low) switch
    {
        ({ } h, { } l) => $"{h:0}°/{l:0}°",
        ({ } h, null) => $"{h:0}°",
        _ => "—",
    };

    private void ApplyForeground(IBrush? brush)
    {
        SetForeground(ConditionIcon, brush);
        SetForeground(TempText, brush);
        SetForeground(ConditionText, brush);
        SetForeground(WindIcon, brush);
        SetForeground(WindText, brush);
        SetForeground(HumidityIcon, brush);
        SetForeground(HumidityText, brush);
    }

    // ClearValue (not Foreground = null) so a disabled background reverts to the theme's
    // default text color instead of local-valuing Foreground to an actual null brush.
    private static void SetForeground(TextBlock control, IBrush? brush)
    {
        if (brush is null) control.ClearValue(TextBlock.ForegroundProperty);
        else control.Foreground = brush;
    }

    private static void SetForeground(PathIcon control, IBrush? brush)
    {
        if (brush is null) control.ClearValue(PathIcon.ForegroundProperty);
        else control.Foreground = brush;
    }
}
