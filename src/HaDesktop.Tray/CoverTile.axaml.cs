using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

/// <summary>Open/stop/close is a clearer interaction for covers than a single on/off toggle, which is ambiguous mid-travel.</summary>
public partial class CoverTile : UserControl
{
    // cover.CoverEntityFeature bit flags (Home Assistant core).
    [Flags]
    private enum Feature
    {
        Open = 1,
        Close = 2,
        SetPosition = 4,
        Stop = 8,
    }

    public string? EntityId { get; set; }

    public event EventHandler? OpenRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? CloseRequested;

    public CoverTile()
    {
        InitializeComponent();
        this.FindControl<PathIcon>("OpenIcon")!.Data = Geometry.Parse(TileIcons.PathFor("chevron-up"));
        this.FindControl<PathIcon>("StopIcon")!.Data = Geometry.Parse(TileIcons.PathFor("stop"));
        this.FindControl<PathIcon>("CloseIcon")!.Data = Geometry.Parse(TileIcons.PathFor("chevron-down"));
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Sets icon, label, and current open/closed status, and — matching Home Assistant's own
    /// cover card — disables Open while already open/opening and Close while already closed/closing,
    /// so you can't queue a no-op move against a cover already at that end of travel.</summary>
    public void SetContent(HaEntityState state, string label)
    {
        this.FindControl<PathIcon>("CoverIcon")!.Data = Geometry.Parse(TileIcons.PathFor(HaEntityDisplay.IconFor(state)));
        this.FindControl<TextBlock>("LabelText")!.Text = label;
        this.FindControl<TextBlock>("StatusText")!.Text = StatusTextFor(state);

        var features = state.Attributes.TryGetValue("supported_features", out var sf) && sf is not null
            ? (Feature)Convert.ToInt64(sf)
            : Feature.Open | Feature.Close | Feature.Stop; // assume full control if the entity doesn't report a bitmask

        var isOpen = state.State is "open" or "opening";
        var isClosed = state.State is "closed" or "closing";

        this.FindControl<Button>("OpenButton")!.IsEnabled = features.HasFlag(Feature.Open) && !isOpen;
        this.FindControl<Button>("CloseButton")!.IsEnabled = features.HasFlag(Feature.Close) && !isClosed;
        this.FindControl<Button>("StopButton")!.IsEnabled = features.HasFlag(Feature.Stop);
    }

    private static string StatusTextFor(HaEntityState state) => state.State switch
    {
        "opening" => Loc.Instance.Tr("Cover.StatusOpening"),
        "closing" => Loc.Instance.Tr("Cover.StatusClosing"),
        "open" => state.Attributes.TryGetValue("current_position", out var p) && p is not null
            ? Loc.Instance.Tr("Cover.StatusOpenAt", Convert.ToInt32(p))
            : Loc.Instance.Tr("Cover.StatusOpen"),
        "closed" => Loc.Instance.Tr("Cover.StatusClosed"),
        "unavailable" => Loc.Instance.Tr("Cover.StatusUnavailable"),
        _ => Loc.Instance.Tr("Cover.StatusUnknown"),
    };

    public void SetCornerRadius(double radius) =>
        this.FindControl<Border>("RootBorder")!.CornerRadius = new Avalonia.CornerRadius(radius);

    /// <summary>Overrides the tile's background with a user-picked color; a fresh tile instance already shows the theme default otherwise (see FlyoutWindow — tiles are rebuilt from scratch on every refresh), so this only ever needs to act when a color is actually set.</summary>
    public void SetCustomColor(Color? color)
    {
        if (color is { } c) this.FindControl<Border>("RootBorder")!.Background = new SolidColorBrush(c);
    }

    public void SetSize(TileSize size)
    {
        Width = TileDimensions.WidthFor(size);
        Height = TileDimensions.HeightFor(size);
    }

    private void OnOpenClicked(object? sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);
    private void OnStopClicked(object? sender, RoutedEventArgs e) => StopRequested?.Invoke(this, EventArgs.Empty);
    private void OnCloseClicked(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
