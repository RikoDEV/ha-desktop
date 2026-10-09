using System;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>Left-click cycles through the entity's supported hvac_modes (e.g. off → heat → cool → off), matching how a physical thermostat mode button behaves. Right-click opens <see cref="ThermostatDetailFlyout"/> for target temperature and preset controls.</summary>
public partial class ClimateTile : UserControl, IEntityTile
{
    private TileConfig? _config;
    private HaEntityState? _state;

    public ClimateTile()
    {
        InitializeComponent();
        RootButton.AddHandler(PointerPressedEvent, OnRootPointerPressed, handledEventsToo: true);
    }

    public void Configure(TileConfig config, double cornerRadius)
    {
        _config = config;
        RootButton.CornerRadius = new Avalonia.CornerRadius(cornerRadius);
        if (TileDimensions.CustomBrushFor(config) is { } brush) RootButton.Background = brush;
        this.SetTileSize(config.Size);
    }

    public void Update(HaEntityState state)
    {
        _state = state;

        var label = _config?.CustomLabel ?? HaEntityDisplay.LabelFor(state);
        IconIcon.Data = TileIcons.GeometryFor(HaEntityDisplay.IconFor(state));
        ValueText.Text = HaEntityDisplay.ClimateTemperatureFor(state);
        LabelText.Text = $"{label} · {HaEntityDisplay.PrettifyHvacMode(state.State)}";
    }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (_state is null) return;

        var modes = HaEntityDisplay.StringListAttribute(_state, "hvac_modes");
        if (modes.Length == 0) return;

        var next = modes[(Array.IndexOf(modes, _state.State) + 1) % modes.Length];
        _ = HaActions.CallAsync("climate", "set_hvac_mode", _state.EntityId, new JsonObject { ["hvac_mode"] = next });
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        e.Handled = true; // don't let it also register as a mode-cycle click

        if (_state is not null) ThermostatDetailFlyout.Show(this, _state);
    }
}
