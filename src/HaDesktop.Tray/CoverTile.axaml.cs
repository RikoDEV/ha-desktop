using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

/// <summary>Open/stop/close is a clearer interaction for covers than a single on/off toggle, which is ambiguous mid-travel.</summary>
public partial class CoverTile : UserControl, IEntityTile
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

    private TileConfig? _config;
    private string? _entityId;

    public CoverTile()
    {
        InitializeComponent();
        OpenIcon.Data = TileIcons.GeometryFor("chevron-up");
        StopIcon.Data = TileIcons.GeometryFor("stop");
        CloseIcon.Data = TileIcons.GeometryFor("chevron-down");
    }

    public void Configure(TileConfig config, double cornerRadius)
    {
        _config = config;
        RootBorder.CornerRadius = new Avalonia.CornerRadius(cornerRadius);
        if (TileDimensions.CustomBrushFor(config) is { } brush) RootBorder.Background = brush;
        this.SetTileSize(config.Size);
    }

    /// <summary>Sets icon, label, and current open/closed status, and — matching Home Assistant's own
    /// cover card — disables Open while already open/opening and Close while already closed/closing,
    /// so you can't queue a no-op move against a cover already at that end of travel.</summary>
    public void Update(HaEntityState state)
    {
        _entityId = state.EntityId;

        CoverIcon.Data = TileIcons.GeometryFor(HaEntityDisplay.IconFor(state));
        LabelText.Text = _config?.CustomLabel ?? HaEntityDisplay.LabelFor(state);
        StatusText.Text = StatusTextFor(state);

        var features = state.Attributes.TryGetValue("supported_features", out var sf) && sf is not null
            ? (Feature)Convert.ToInt64(sf)
            : Feature.Open | Feature.Close | Feature.Stop; // assume full control if the entity doesn't report a bitmask

        var isOpen = state.State is "open" or "opening";
        var isClosed = state.State is "closed" or "closing";

        OpenButton.IsEnabled = features.HasFlag(Feature.Open) && !isOpen;
        CloseButton.IsEnabled = features.HasFlag(Feature.Close) && !isClosed;
        StopButton.IsEnabled = features.HasFlag(Feature.Stop);
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

    private void OnOpenClicked(object? sender, RoutedEventArgs e) => Call("open_cover");
    private void OnStopClicked(object? sender, RoutedEventArgs e) => Call("stop_cover");
    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Call("close_cover");

    private void Call(string service)
    {
        if (_entityId is not null) _ = HaActions.CallAsync("cover", service, _entityId);
    }
}
