using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>
/// One Android-quick-settings-style tile: icon + label + on/off state.
/// Left-click toggles via EntityId; right-click raises DetailRequested for
/// callers that want to show extra controls (e.g. a brightness/color flyout
/// for lights) without complicating this control with domain-specific UI.
/// </summary>
public partial class QuickToggleTile : UserControl
{
    public static readonly Avalonia.StyledProperty<string?> EntityIdProperty =
        Avalonia.AvaloniaProperty.Register<QuickToggleTile, string?>(nameof(EntityId));

    public string? EntityId
    {
        get => GetValue(EntityIdProperty);
        set => SetValue(EntityIdProperty, value);
    }

    public event EventHandler<bool>? Toggled;
    public event EventHandler? DetailRequested;

    // Persists across repeated SetContent calls (every incoming state_changed event re-runs it),
    // unlike tintColor below which SetContent receives fresh each time — a custom color is a
    // standing user choice for this tile, not something derived from the entity's current state.
    private Color? _customColor;

    public QuickToggleTile()
    {
        InitializeComponent();
        var toggle = this.FindControl<ToggleButton>("Toggle")!;
        toggle.AddHandler(PointerPressedEvent, OnTogglePointerPressed, handledEventsToo: true);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <param name="iconKey">A key into <see cref="TileIcons.Paths"/>, not a display glyph.</param>
    /// <param name="tintColor">A light's current rgb_color, if it's on and reports one — tints the tile to match instead of the generic accent color. Ignored in favor of <see cref="SetCustomColor"/> if the user picked one, since that's a deliberate override.</param>
    public void SetContent(string iconKey, string label, bool isOn, Color? tintColor = null)
    {
        this.FindControl<PathIcon>("IconIcon")!.Data = Geometry.Parse(TileIcons.PathFor(iconKey));
        this.FindControl<TextBlock>("LabelText")!.Text = label;

        var toggle = this.FindControl<ToggleButton>("Toggle")!;
        toggle.IsChecked = isOn;

        ApplyColor(_customColor ?? (isOn ? tintColor : null), alwaysOn: _customColor is not null);
    }

    /// <summary>Overrides this tile's color with a user pick from Settings, shown regardless of on/off state (unlike a light's own tintColor, which only ever tints the checked/on appearance).</summary>
    public void SetCustomColor(Color? color)
    {
        _customColor = color;
        ApplyColor(color, alwaysOn: color is not null);
    }

    private void ApplyColor(Color? color, bool alwaysOn)
    {
        var toggle = this.FindControl<ToggleButton>("Toggle")!;
        var icon = this.FindControl<PathIcon>("IconIcon")!;
        var labelText = this.FindControl<TextBlock>("LabelText")!;

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
            toggle.Resources["ToggleButtonBackgroundChecked"] = brush;
            toggle.Resources["ToggleButtonBackgroundCheckedPointerOver"] = brush;
            toggle.Resources["ToggleButtonBackgroundCheckedPressed"] = brush;
            // The unchecked/off appearance IS a plain TemplateBinding to Background, though —
            // a custom color (unlike a light's live tint) is meant to show even when off.
            if (alwaysOn) toggle.Background = brush;
            else toggle.ClearValue(ToggleButton.BackgroundProperty);

            var foreground = IsColorDark(c) ? Brushes.White : Brushes.Black;
            icon.Foreground = foreground;
            labelText.Foreground = foreground;
        }
        else
        {
            toggle.Resources.Remove("ToggleButtonBackgroundChecked");
            toggle.Resources.Remove("ToggleButtonBackgroundCheckedPointerOver");
            toggle.Resources.Remove("ToggleButtonBackgroundCheckedPressed");
            toggle.ClearValue(ToggleButton.BackgroundProperty);

            // Reverts to the theme's normal unchecked foreground instead of local-valuing it
            // to an actual null brush.
            icon.ClearValue(PathIcon.ForegroundProperty);
            labelText.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private static bool IsColorDark(Color color)
    {
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        return luminance < 0.55;
    }

    public void SetCornerRadius(double radius) =>
        this.FindControl<ToggleButton>("Toggle")!.CornerRadius = new Avalonia.CornerRadius(radius);

    public void SetSize(TileSize size)
    {
        Width = TileDimensions.WidthFor(size);
        Height = TileDimensions.HeightFor(size);
    }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        var isOn = this.FindControl<ToggleButton>("Toggle")!.IsChecked ?? false;
        Toggled?.Invoke(this, isOn);
    }

    private void OnTogglePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        e.Handled = true; // don't let it also register as a toggle click
        DetailRequested?.Invoke(this, EventArgs.Empty);
    }
}
