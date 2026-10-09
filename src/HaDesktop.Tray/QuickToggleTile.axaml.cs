using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>
/// One Android-quick-settings-style tile: icon + label + on/off state.
/// Left-click toggles the entity; right-click opens a detail popup for the domains that have one
/// (brightness/color for a light, target humidity and mode for a humidifier).
/// </summary>
public partial class QuickToggleTile : UserControl, IEntityTile
{
    private TileConfig? _config;
    private HaEntityState? _state;

    // A standing user choice for this tile, unlike a light's own color, which is derived from the
    // entity's current state on every update.
    private Color? _customColor;

    public QuickToggleTile()
    {
        InitializeComponent();
        Toggle.AddHandler(PointerPressedEvent, OnTogglePointerPressed, handledEventsToo: true);
    }

    public void Configure(TileConfig config, double cornerRadius)
    {
        _config = config;
        _customColor = config.CustomColor is { } hex && Color.TryParse(hex, out var color) ? color : null;
        Toggle.CornerRadius = new Avalonia.CornerRadius(cornerRadius);
        this.SetTileSize(config.Size);
    }

    public void Update(HaEntityState state)
    {
        _state = state;

        IconIcon.Data = TileIcons.GeometryFor(_config?.CustomIcon ?? HaEntityDisplay.IconFor(state));
        LabelText.Text = _config?.CustomLabel ?? HaEntityDisplay.LabelFor(state);
        Toggle.IsChecked = state.IsOn;

        // A light's current rgb_color tints the tile to match while it's on, instead of the generic
        // accent color. A custom color is a deliberate override: it wins, and shows even when off.
        ApplyColor(_customColor ?? HaEntityDisplay.LightColorFor(state), alwaysOn: _customColor is not null);
    }

    private void ApplyColor(Color? color, bool alwaysOn)
    {
        if (color is { } c)
        {
            // FluentAvaloniaUI's checked-state ToggleButton visual doesn't come from a
            // TemplateBinding to this control's own Background — it's a Style selector that
            // sets the *named template part's* Background straight from the
            // ToggleButtonBackgroundChecked(/PointerOver/Pressed) DynamicResource, so setting
            // Background on the instance has no visible effect while checked. Putting matching
            // keys in this instance's own Resources overrides the DynamicResource lookup for
            // just this tile, without touching every other toggle in the app.
            var brush = new SolidColorBrush(c);
            Toggle.Resources["ToggleButtonBackgroundChecked"] = brush;
            Toggle.Resources["ToggleButtonBackgroundCheckedPointerOver"] = brush;
            Toggle.Resources["ToggleButtonBackgroundCheckedPressed"] = brush;
            // The unchecked/off appearance IS a plain TemplateBinding to Background, though —
            // a custom color (unlike a light's live tint) is meant to show even when off.
            if (alwaysOn) Toggle.Background = brush;
            else Toggle.ClearValue(ToggleButton.BackgroundProperty);

            var foreground = IsColorDark(c) ? Brushes.White : Brushes.Black;
            IconIcon.Foreground = foreground;
            LabelText.Foreground = foreground;
        }
        else
        {
            Toggle.Resources.Remove("ToggleButtonBackgroundChecked");
            Toggle.Resources.Remove("ToggleButtonBackgroundCheckedPointerOver");
            Toggle.Resources.Remove("ToggleButtonBackgroundCheckedPressed");
            Toggle.ClearValue(ToggleButton.BackgroundProperty);

            // Reverts to the theme's normal unchecked foreground instead of local-valuing it
            // to an actual null brush.
            IconIcon.ClearValue(PathIcon.ForegroundProperty);
            LabelText.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private static bool IsColorDark(Color color)
    {
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        return luminance < 0.55;
    }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (_state is not null) _ = HaActions.ToggleAsync(_state);
    }

    private void OnTogglePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        e.Handled = true; // don't let it also register as a toggle click

        if (_state?.Domain == "light") LightDetailFlyout.Show(this, _state);
        else if (_state?.Domain == "humidifier") HumidifierDetailFlyout.Show(this, _state);
    }
}
