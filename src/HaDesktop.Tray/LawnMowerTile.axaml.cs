using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

/// <summary>Start/pause/dock is a clearer interaction for a mower than a single on/off toggle — mirrors CoverTile's open/stop/close.</summary>
public partial class LawnMowerTile : UserControl
{
    // lawn_mower.LawnMowerEntityFeature bit flags (Home Assistant core).
    [Flags]
    private enum Feature
    {
        StartMowing = 1,
        Pause = 2,
        Dock = 4,
    }

    public string? EntityId { get; set; }

    public event EventHandler? StartRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? DockRequested;

    public LawnMowerTile()
    {
        InitializeComponent();
        this.FindControl<PathIcon>("StartIcon")!.Data = Geometry.Parse(TileIcons.PathFor("chevron-up"));
        this.FindControl<PathIcon>("PauseIcon")!.Data = Geometry.Parse(TileIcons.PathFor("pause"));
        this.FindControl<PathIcon>("DockIcon")!.Data = Geometry.Parse(TileIcons.PathFor("home"));
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Sets icon, label, and current status, and disables actions the entity doesn't currently support per its state/feature bitmask.</summary>
    public void SetContent(HaEntityState state, string label)
    {
        this.FindControl<PathIcon>("MowerIcon")!.Data = Geometry.Parse(TileIcons.PathFor(HaEntityDisplay.IconFor(state)));
        this.FindControl<TextBlock>("LabelText")!.Text = label;
        this.FindControl<TextBlock>("StatusText")!.Text = HaEntityDisplay.LawnMowerStatusFor(state);

        var features = state.Attributes.TryGetValue("supported_features", out var sf) && sf is not null
            ? (Feature)Convert.ToInt64(sf)
            : Feature.StartMowing | Feature.Pause | Feature.Dock; // assume full control if the entity doesn't report a bitmask

        var isMowing = state.State is "mowing" or "returning";
        var isDocked = state.State == "docked";

        this.FindControl<Button>("StartButton")!.IsEnabled = features.HasFlag(Feature.StartMowing) && !isMowing;
        this.FindControl<Button>("PauseButton")!.IsEnabled = features.HasFlag(Feature.Pause) && isMowing;
        this.FindControl<Button>("DockButton")!.IsEnabled = features.HasFlag(Feature.Dock) && !isDocked;
    }

    public void SetCornerRadius(double radius) =>
        this.FindControl<Border>("RootBorder")!.CornerRadius = new Avalonia.CornerRadius(radius);

    public void SetSize(TileSize size) => Width = size == TileSize.Wide ? 184 : 88;

    private void OnStartClicked(object? sender, RoutedEventArgs e) => StartRequested?.Invoke(this, EventArgs.Empty);
    private void OnPauseClicked(object? sender, RoutedEventArgs e) => PauseRequested?.Invoke(this, EventArgs.Empty);
    private void OnDockClicked(object? sender, RoutedEventArgs e) => DockRequested?.Invoke(this, EventArgs.Empty);
}
