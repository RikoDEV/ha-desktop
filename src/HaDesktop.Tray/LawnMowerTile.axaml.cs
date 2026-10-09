using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>Start/pause/dock is a clearer interaction for a mower than a single on/off toggle — mirrors CoverTile's open/stop/close.</summary>
public partial class LawnMowerTile : UserControl, IEntityTile
{
    // lawn_mower.LawnMowerEntityFeature bit flags (Home Assistant core).
    [Flags]
    private enum Feature
    {
        StartMowing = 1,
        Pause = 2,
        Dock = 4,
    }

    private TileConfig? _config;
    private string? _entityId;

    public LawnMowerTile()
    {
        InitializeComponent();
        StartIcon.Data = TileIcons.GeometryFor("chevron-up");
        PauseIcon.Data = TileIcons.GeometryFor("pause");
        DockIcon.Data = TileIcons.GeometryFor("home");
    }

    public void Configure(TileConfig config, double cornerRadius)
    {
        _config = config;
        RootBorder.CornerRadius = new Avalonia.CornerRadius(cornerRadius);
        if (TileDimensions.CustomBrushFor(config) is { } brush) RootBorder.Background = brush;
        this.SetTileSize(config.Size);
    }

    /// <summary>Sets icon, label, and current status, and disables actions the entity doesn't currently support per its state/feature bitmask.</summary>
    public void Update(HaEntityState state)
    {
        _entityId = state.EntityId;

        MowerIcon.Data = TileIcons.GeometryFor(HaEntityDisplay.IconFor(state));
        LabelText.Text = _config?.CustomLabel ?? HaEntityDisplay.LabelFor(state);
        StatusText.Text = HaEntityDisplay.LawnMowerStatusFor(state);

        var features = state.Attributes.TryGetValue("supported_features", out var sf) && sf is not null
            ? (Feature)Convert.ToInt64(sf)
            : Feature.StartMowing | Feature.Pause | Feature.Dock; // assume full control if the entity doesn't report a bitmask

        var isMowing = state.State is "mowing" or "returning";
        var isDocked = state.State == "docked";

        StartButton.IsEnabled = features.HasFlag(Feature.StartMowing) && !isMowing;
        PauseButton.IsEnabled = features.HasFlag(Feature.Pause) && isMowing;
        DockButton.IsEnabled = features.HasFlag(Feature.Dock) && !isDocked;
    }

    private void OnStartClicked(object? sender, RoutedEventArgs e) => Call("start_mowing");
    private void OnPauseClicked(object? sender, RoutedEventArgs e) => Call("pause");
    private void OnDockClicked(object? sender, RoutedEventArgs e) => Call("dock");

    private void Call(string service)
    {
        if (_entityId is not null) _ = HaActions.CallAsync("lawn_mower", service, _entityId);
    }
}
