using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace HaDesktop.Tray;

/// <summary>Runs the most recently scheduled action once input has been quiet for a moment, so dragging a slider or tapping a stepper sends Home Assistant one service call instead of one per intermediate value.</summary>
internal sealed class Debouncer
{
    private readonly DispatcherTimer _timer;
    private Func<Task>? _pending;

    public Debouncer(int delayMilliseconds = 250)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMilliseconds) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Stop();
            var action = _pending;
            _pending = null;
            if (action is not null) await action();
        };
    }

    public void Schedule(Func<Task> action)
    {
        _pending = action;
        _timer.Stop();
        _timer.Start();
    }
}

/// <summary>Building blocks shared by the popups that open from a tile.</summary>
internal static class DetailFlyoutControls
{
    public static Flyout Show(Control anchor, Control content)
    {
        var flyout = new Flyout { Content = content, Placement = PlacementMode.Bottom };
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
        return flyout;
    }

    /// <summary>
    /// A -/value/+ row under a small label. Changes are debounced before <paramref name="onChanged"/>
    /// runs. With <paramref name="snapToStep"/>, each press also rounds the value onto the step grid
    /// (a thermostat set to 21.3 with a 0.5 step goes to 21.5, not 21.8).
    /// </summary>
    public static Control BuildStepper(string label, double initial, double min, double max, double step, bool snapToStep, Func<double, string> format, Func<double, Task> onChanged)
    {
        var value = initial;
        var valueText = new TextBlock { Text = format(value), FontSize = 15, FontWeight = FontWeight.SemiBold, Width = 56, TextAlignment = TextAlignment.Center };
        var debouncer = new Debouncer();

        void Step(double delta)
        {
            var next = snapToStep ? Math.Round((value + delta) / step) * step : value + delta;
            value = Math.Clamp(next, min, max);
            valueText.Text = format(value);
            debouncer.Schedule(() => onChanged(value));
        }

        var minusButton = BuildStepButton("minus");
        minusButton.Click += (_, _) => Step(-step);

        var plusButton = BuildStepButton("plus");
        plusButton.Click += (_, _) => Step(step);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(minusButton);
        row.Children.Add(valueText);
        row.Children.Add(plusButton);

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 });
        panel.Children.Add(row);
        return panel;
    }

    private static Button BuildStepButton(string iconKey) => new()
    {
        Content = new PathIcon { Data = TileIcons.GeometryFor(iconKey), Width = 14, Height = 14 },
        Width = 32,
        Height = 32,
        Padding = new Avalonia.Thickness(0),
    };

    /// <summary>A row of toggle-style buttons, one per mode, with the current one pre-highlighted and re-highlighted immediately on click (the flyout has no live state feed of its own).</summary>
    public static Control BuildModeRow(string[] modes, string? currentMode, Func<string, string> displayNameFor, Func<string, Task> onSelect)
    {
        var panel = new WrapPanel();
        var buttons = new List<Button>();

        foreach (var mode in modes)
        {
            var button = new Button { Content = displayNameFor(mode), Margin = new Avalonia.Thickness(0, 0, 4, 4), Padding = new Avalonia.Thickness(8, 4) };
            if (mode == currentMode) button.Classes.Add("accent");

            button.Click += async (_, _) =>
            {
                foreach (var other in buttons) other.Classes.Remove("accent");
                button.Classes.Add("accent");
                await onSelect(mode);
            };

            buttons.Add(button);
            panel.Children.Add(button);
        }

        return panel;
    }

    public static TextBlock BuildSectionLabel(string text, bool spaced = true) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.7,
        Margin = new Avalonia.Thickness(0, spaced ? 4 : 0, 0, 0),
    };
}
