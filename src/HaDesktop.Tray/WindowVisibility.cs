using System;
using Avalonia;
using Avalonia.Controls;

namespace HaDesktop.Tray;

internal static class WindowVisibility
{
    /// <summary>
    /// Calls <paramref name="onChanged"/> with whether <paramref name="control"/> is actually on
    /// screen: attached to a window that is currently shown. The flyout is hidden rather than
    /// closed, so its content stays in the visual tree around the clock — anything that polls or
    /// animates has to stop on this signal, not on detaching.
    /// </summary>
    public static void Track(Control control, Action<bool> onChanged)
    {
        Window? window = null;

        void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.IsVisibleProperty) onChanged(window!.IsVisible);
        }

        control.AttachedToVisualTree += (_, _) =>
        {
            window = TopLevel.GetTopLevel(control) as Window;
            if (window is not null) window.PropertyChanged += OnWindowPropertyChanged;
            onChanged(window?.IsVisible ?? true);
        };

        control.DetachedFromVisualTree += (_, _) =>
        {
            if (window is not null) window.PropertyChanged -= OnWindowPropertyChanged;
            window = null;
            onChanged(false);
        };
    }
}
