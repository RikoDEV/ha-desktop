using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>Left-click cycles through the entity's supported hvac_modes (e.g. off → heat → cool → off), matching how a physical thermostat mode button behaves. Right-click opens <see cref="ThermostatDetailFlyout"/> for target temperature and preset controls.</summary>
public partial class ClimateTile : UserControl
{
    public string? EntityId { get; set; }

    /// <summary>Raised with the hvac_mode to switch to next, on left-click.</summary>
    public event EventHandler<string>? ModeChangeRequested;

    /// <summary>Raised on right-click, mirroring QuickToggleTile's light detail popup.</summary>
    public event EventHandler? DetailRequested;

    private string[] _hvacModes = Array.Empty<string>();
    private string _currentMode = "off";

    public ClimateTile()
    {
        InitializeComponent();
        var root = this.FindControl<Button>("RootButton")!;
        root.AddHandler(PointerPressedEvent, OnRootPointerPressed, handledEventsToo: true);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void SetContent(HaEntityState state, string label)
    {
        this.FindControl<PathIcon>("IconIcon")!.Data = Geometry.Parse(TileIcons.PathFor(HaEntityDisplay.IconFor(state)));
        this.FindControl<TextBlock>("ValueText")!.Text = HaEntityDisplay.ClimateTemperatureFor(state);
        this.FindControl<TextBlock>("LabelText")!.Text = $"{label} · {HaEntityDisplay.PrettifyHvacMode(state.State)}";

        _currentMode = state.State;
        _hvacModes = HaEntityDisplay.StringListAttribute(state, "hvac_modes");
    }

    public void SetCornerRadius(double radius) =>
        this.FindControl<Button>("RootButton")!.CornerRadius = new Avalonia.CornerRadius(radius);

    /// <summary>Overrides the tile's background with a user-picked color; a fresh tile instance already shows the theme default otherwise (see FlyoutWindow — tiles are rebuilt from scratch on every refresh), so this only ever needs to act when a color is actually set.</summary>
    public void SetCustomColor(Color? color)
    {
        if (color is { } c) this.FindControl<Button>("RootButton")!.Background = new SolidColorBrush(c);
    }

    public void SetSize(TileSize size)
    {
        Width = TileDimensions.WidthFor(size);
        Height = TileDimensions.HeightFor(size);
    }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (_hvacModes.Length == 0) return;

        var currentIndex = Array.IndexOf(_hvacModes, _currentMode);
        var next = _hvacModes[(currentIndex + 1) % _hvacModes.Length];
        ModeChangeRequested?.Invoke(this, next);
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        e.Handled = true; // don't let it also register as a mode-cycle click
        DetailRequested?.Invoke(this, EventArgs.Empty);
    }
}
