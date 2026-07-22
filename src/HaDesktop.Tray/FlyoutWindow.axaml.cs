using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

/// <summary>
/// The Android-quick-settings-style popup that opens from the tray icon.
/// Tiles come from the live HA connection: lights/switches get a tap-to-toggle
/// tile (right-click for brightness/color on lights), covers get dedicated
/// open/stop/close buttons since a single toggle is ambiguous mid-travel.
/// </summary>
public partial class FlyoutWindow : Window
{
    private readonly Dictionary<string, QuickToggleTile> _toggleTilesByEntityId = new();
    private readonly Dictionary<string, CoverTile> _coverTilesByEntityId = new();
    private readonly Dictionary<string, SensorTile> _sensorTilesByEntityId = new();
    private readonly Dictionary<string, GaugeTile> _gaugeTilesByEntityId = new();
    private readonly Dictionary<string, CameraTile> _cameraTilesByEntityId = new();
    private readonly Dictionary<string, ClimateTile> _climateTilesByEntityId = new();
    private readonly Dictionary<string, LawnMowerTile> _lawnMowerTilesByEntityId = new();
    // Keyed by *child* entity id, not the group's own synthetic id — a state_changed event only
    // ever names a real entity, so this is what OnEntityStateChanged can actually look up.
    private readonly Dictionary<string, GroupTile> _groupTilesByChildEntityId = new();
    private readonly Dictionary<string, TileConfig> _tileConfigsByEntityId = new();
    private readonly WeatherWidget _weatherWidget = new();
    private readonly MediaPlayerWidget _mediaPlayerWidget = new();
    private string? _currentMediaPlayerEntityId;

    // Kept in sync with every state_changed event so a single incoming media_player update
    // can re-run the same "pick the best player" logic RefreshTilesAsync uses, without
    // needing a full GetStatesAsync round-trip just to notice playback started.
    private readonly Dictionary<string, HaEntityState> _lastKnownStates = new();

    // Matches the 88px tile width + 4px margin on both sides used by every tile's own SetSize.
    private const double CellWidth = 96;
    // A Wide/Group tile's 2-column span needs at least this many columns to render without
    // TileLayoutCompactor.Compact clamping it down to a degraded 1-column shape.
    private const int MinLiveColumns = 2;
    // The outer Border's Padding, the one thing between the window's client width and the actual
    // content area — an estimate, not measured, since ClientSize is the one number available before
    // the next layout pass actually happens.
    private const double HorizontalChrome = 24;

    // Weather/media widgets sit side by side (in columns, via a WrapPanel) once there's room for
    // two this wide with WidgetSpacing between them; otherwise each takes the full row, stacked,
    // same as before this app supported resizing at all. 220 is MediaPlayerWidget's practical
    // floor: a 40px album-art thumbnail + 4 icon buttons (~28px each) already eats ~150px before
    // the title/artist text gets any room at all.
    private const double MinWidgetCardWidth = 220;
    private const double WidgetSpacing = 8;

    // However many columns currently fit the window's width — recomputed on resize (see OnResized)
    // and used to re-flow _lastConfigs into more or fewer columns without touching the persisted
    // (fixed 3-column) layout the Settings tile editor works with.
    private int _liveColumnCount;
    private List<TileConfig> _lastConfigs = new();
    private DispatcherTimer? _sizeSaveDebounce;

    /// <summary>Raised when the user asks to open Settings from within the flyout (e.g. the "not connected" state or the header button).</summary>
    public event Action? OpenSettingsRequested;

    public FlyoutWindow()
    {
        InitializeComponent();
        this.FindControl<PathIcon>("SettingsIcon")!.Data = Geometry.Parse(TileIcons.PathFor("cog"));
        this.FindControl<PathIcon>("NotificationsIcon")!.Data = Geometry.Parse(TileIcons.PathFor("bell"));
        _liveColumnCount = ComputeColumnCount(Width);
        Deactivated += (_, _) => Hide();
        AppSettings.ConnectionChanged += OnConnectionChanged;
        AppSettings.LocalPreferencesLoaded += OnLocalPreferencesLoaded;
        Loc.Instance.LanguageChanged += OnConnectionChanged;
        Resized += OnResized;
        AttachResizeHandlers();
        _ = RefreshTilesAsync();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Which of the window's 4 corners is anchored next to the tray icon/menu bar item — see <see cref="DetermineAnchorCorner"/>.</summary>
    private enum AnchorCorner { BottomRight, BottomLeft, TopRight, TopLeft }

    /// <summary>Every resize-handle name, keyed by the corner whose 2 edges + 1 diagonal corner should stay enabled — the other 5 handles get disabled so dragging them can't pull the anchored corner away from the tray icon. See ApplyAnchorCorner.</summary>
    private static readonly Dictionary<AnchorCorner, string[]> EnabledHandlesByAnchor = new()
    {
        [AnchorCorner.BottomRight] = new[] { "ResizeWest", "ResizeNorth", "ResizeNorthWest" },
        [AnchorCorner.BottomLeft] = new[] { "ResizeEast", "ResizeNorth", "ResizeNorthEast" },
        [AnchorCorner.TopRight] = new[] { "ResizeWest", "ResizeSouth", "ResizeSouthWest" },
        [AnchorCorner.TopLeft] = new[] { "ResizeEast", "ResizeSouth", "ResizeSouthEast" },
    };

    /// <summary>
    /// SystemDecorations="None" drops the OS's own resizable border along with its chrome, and
    /// Avalonia doesn't grow one back just because CanResize="True" — without this, the window can
    /// still be resized programmatically (e.g. restoring a saved size) but the user has no edge to
    /// grab, and no cursor ever changes to suggest one exists. The XAML overlays a thin transparent
    /// strip/square per edge/corner; this wires all 8 to their matching WindowEdge once — which of
    /// them are actually usable at any given moment is toggled separately by ApplyAnchorCorner.
    /// </summary>
    private void AttachResizeHandlers()
    {
        AttachResizeHandle("ResizeWest", WindowEdge.West);
        AttachResizeHandle("ResizeEast", WindowEdge.East);
        AttachResizeHandle("ResizeNorth", WindowEdge.North);
        AttachResizeHandle("ResizeSouth", WindowEdge.South);
        AttachResizeHandle("ResizeNorthWest", WindowEdge.NorthWest);
        AttachResizeHandle("ResizeNorthEast", WindowEdge.NorthEast);
        AttachResizeHandle("ResizeSouthWest", WindowEdge.SouthWest);
        AttachResizeHandle("ResizeSouthEast", WindowEdge.SouthEast);
    }

    private void AttachResizeHandle(string name, WindowEdge edge)
    {
        var handle = this.FindControl<Border>(name)!;
        handle.PointerPressed += (_, e) =>
        {
            if (handle.IsHitTestVisible && e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
                BeginResizeDrag(edge, e);
        };
    }

    /// <summary>Enables only the 3 handles next to <paramref name="corner"/>'s opposite corner (see EnabledHandlesByAnchor), disabling the rest — IsHitTestVisible="False" also stops them from showing their resize cursor.</summary>
    private void ApplyAnchorCorner(AnchorCorner corner)
    {
        var enabled = EnabledHandlesByAnchor[corner];
        foreach (var names in EnabledHandlesByAnchor.Values)
            foreach (var name in names)
                this.FindControl<Border>(name)!.IsHitTestVisible = enabled.Contains(name);
    }

    /// <summary>
    /// Infers which corner of the screen the tray icon/menu bar item sits near, from how the
    /// screen's WorkingArea is inset from its full Bounds — the taskbar/panel/menu bar occupies
    /// that inset. macOS is special-cased rather than measured: its Dock (bottom by default, but
    /// resizable/repositionable) can easily be taller than the menu bar, so comparing inset sizes
    /// would misdetect it — the menu bar (and every status item) is always along the top edge
    /// regardless of the Dock, so top-right is unconditionally correct there.
    /// </summary>
    private static AnchorCorner DetermineAnchorCorner(Screen screen)
    {
        if (OperatingSystem.IsMacOS())
            return AnchorCorner.TopRight;

        var work = screen.WorkingArea;
        var bounds = screen.Bounds;

        var topInset = work.Y - bounds.Y;
        var bottomInset = bounds.Bottom - work.Bottom;
        var leftInset = work.X - bounds.X;
        var rightInset = bounds.Right - work.Right;
        var maxInset = Math.Max(Math.Max(topInset, bottomInset), Math.Max(leftInset, rightInset));

        if (maxInset <= 0) return AnchorCorner.BottomRight; // no taskbar/panel detected (e.g. auto-hide) — sane default
        if (topInset == maxInset) return AnchorCorner.TopRight; // top panel (GNOME, etc.) — tray sits at its right end
        if (leftInset == maxInset) return AnchorCorner.BottomLeft; // vertical taskbar on the left — tray at its bottom end
        return AnchorCorner.BottomRight; // bottom taskbar, or a vertical one on the right — tray at its bottom end either way
    }

    private void OnConnectionChanged()
    {
        Dispatcher.UIThread.Post(() => _ = RefreshTilesAsync());
    }

    /// <summary>
    /// FlyoutWindow is constructed (and this constructor already run) before
    /// AppSettings.LoadLocalPreferencesAsync even starts — see App.axaml.cs — so the saved size
    /// can't just be read at construction time; it's applied here once loading actually finishes.
    /// </summary>
    private void OnLocalPreferencesLoaded()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var prefs = AppSettings.FlyoutWindowPrefs;
            Width = prefs.Width;
            Height = prefs.Height;

            LayoutWidgetsRow(Width);

            var newColumnCount = ComputeColumnCount(Width);
            if (newColumnCount == _liveColumnCount) return;
            _liveColumnCount = newColumnCount;
            if (_lastConfigs.Count > 0 && AppSettings.Client is { } client)
                PopulateTileGrid(_lastConfigs, _lastKnownStates, client);
        });
    }

    private void OnResized(object? sender, WindowResizedEventArgs e)
    {
        LayoutWidgetsRow(e.ClientSize.Width);

        var newColumnCount = ComputeColumnCount(e.ClientSize.Width);
        if (newColumnCount != _liveColumnCount && _lastConfigs.Count > 0 && AppSettings.Client is { } client)
        {
            _liveColumnCount = newColumnCount;
            PopulateTileGrid(_lastConfigs, _lastKnownStates, client);
        }

        // Debounced — this event fires continuously while the user drags a resize handle.
        _sizeSaveDebounce?.Stop();
        _sizeSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _sizeSaveDebounce.Tick += async (_, _) =>
        {
            _sizeSaveDebounce!.Stop();
            await AppSettings.SetFlyoutWindowSizeAsync(Width, Height);
        };
        _sizeSaveDebounce.Start();
    }

    private static int ComputeColumnCount(double clientWidth) =>
        Math.Max(MinLiveColumns, (int)Math.Floor((clientWidth - HorizontalChrome) / CellWidth));

    /// <summary>
    /// Places the weather/media widgets side by side (two star columns, splitting the available
    /// width evenly) once there's room for both at a reasonable card width, or stacked (two Auto
    /// rows, each full width) otherwise — called whenever the window is resized or either widget's
    /// visibility changes. Rebuilds WidgetsGrid's own row/column definitions each time rather than
    /// computing a pixel width for each host and trusting a WrapPanel to independently arrive at
    /// the same side-by-side-or-not decision from that.
    /// </summary>
    private void LayoutWidgetsRow(double clientWidth)
    {
        var weatherHost = this.FindControl<ContentControl>("WeatherHost")!;
        var mediaHost = this.FindControl<ContentControl>("MediaPlayerHost")!;
        var widgetsGrid = this.FindControl<Grid>("WidgetsGrid")!;

        var available = Math.Max(0, clientWidth - HorizontalChrome);
        var bothVisible = weatherHost.IsVisible && mediaHost.IsVisible;
        var sideBySide = bothVisible && (available - WidgetSpacing) / 2 >= MinWidgetCardWidth;

        widgetsGrid.ColumnDefinitions.Clear();
        widgetsGrid.RowDefinitions.Clear();

        if (sideBySide)
        {
            widgetsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            widgetsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(weatherHost, 0);
            Grid.SetRow(weatherHost, 0);
            Grid.SetColumn(mediaHost, 1);
            Grid.SetRow(mediaHost, 0);
        }
        else
        {
            widgetsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            widgetsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(weatherHost, 0);
            Grid.SetRow(weatherHost, 0);
            Grid.SetColumn(mediaHost, 0);
            Grid.SetRow(mediaHost, 1);
        }
    }

    private int _tilesRefreshToken;

    private void ClearTileState()
    {
        var grid = this.FindControl<Grid>("TileGrid")!;
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        _toggleTilesByEntityId.Clear();
        _coverTilesByEntityId.Clear();
        _sensorTilesByEntityId.Clear();
        _gaugeTilesByEntityId.Clear();
        _cameraTilesByEntityId.Clear();
        _climateTilesByEntityId.Clear();
        _lawnMowerTilesByEntityId.Clear();
        _groupTilesByChildEntityId.Clear();
        _tileConfigsByEntityId.Clear();
    }

    private void ShowEmptyState(string icon, string title, string subtitle, bool showSettingsButton)
    {
        this.FindControl<Grid>("TileGrid")!.IsVisible = false;
        var host = this.FindControl<ContentControl>("EmptyStateHost")!;
        host.Content = BuildEmptyState(icon, title, subtitle, showSettingsButton);
        host.IsVisible = true;
    }

    private async Task RefreshTilesAsync()
    {
        // AppSettings.ConnectionChanged fires for many unrelated reasons (any settings
        // change, reconnects, etc.) and this method awaits a network call, so overlapping
        // calls are routine. Clearing/rebuilding unconditionally here let an older call
        // resuming after a newer one already rebuilt the list add its own tiles on top —
        // duplicate tile instances for the same entity, only one of which a click/state
        // update would ever touch, making toggles look like they silently do nothing.
        var myToken = ++_tilesRefreshToken;

        var client = AppSettings.Client;
        if (client is null)
        {
            if (myToken != _tilesRefreshToken) return;
            ClearTileState();
            _lastKnownStates.Clear();
            _lastConfigs = new();
            HideWeatherWidget();
            HideMediaPlayerWidget();
            ShowEmptyState("🔌", Loc.Instance.Tr("Flyout.NotConnectedTitle"), Loc.Instance.Tr("Flyout.NotConnectedSubtitle"), showSettingsButton: true);
            return;
        }

        client.StateChanged -= OnEntityStateChanged;
        client.StateChanged += OnEntityStateChanged;

        List<HaEntityState> states;
        try
        {
            states = await client.GetStatesAsync();
        }
        catch
        {
            if (myToken != _tilesRefreshToken) return;
            ClearTileState();
            _lastKnownStates.Clear();
            _lastConfigs = new();
            HideWeatherWidget();
            HideMediaPlayerWidget();
            ShowEmptyState("⚠", Loc.Instance.Tr("Flyout.ConnectionErrorTitle"), Loc.Instance.Tr("Flyout.ConnectionErrorSubtitle"), showSettingsButton: true);
            return;
        }

        if (myToken != _tilesRefreshToken) return; // superseded by a later call while we were awaiting

        ClearTileState();

        var byId = states.ToDictionary(s => s.EntityId);
        _lastKnownStates.Clear();
        foreach (var (id, state) in byId) _lastKnownStates[id] = state;
        UpdateWeatherWidget(byId, client);
        UpdateMediaPlayerWidget(byId, client);
        LayoutWidgetsRow(Width);

        List<TileConfig> configs = AppSettings.SelectedTiles.Count > 0
            // User has picked specific tiles in Settings — show exactly those, at their chosen positions/sizes.
            ? AppSettings.SelectedTiles
            // No selection yet — fall back to a reasonable default so the flyout isn't empty on first connect.
            // Not yet positioned (fresh, ephemeral list), so compacted the same way a persisted list would be.
            : TileLayoutCompactor.Compact(states.Where(s => s.Domain is "light" or "switch" or "cover").Take(8).Select(s => new TileConfig(s.EntityId)).ToList());

        PopulateTileGrid(configs, byId, client);
    }

    /// <summary>
    /// Builds every tile in <paramref name="configs"/> into TileGrid. Called both after a full
    /// RefreshTilesAsync (fresh states from HA) and from a resize that changed how many columns
    /// currently fit (reusing the last known configs/states — no network round-trip needed just to
    /// re-flow the same tiles into a different column count).
    /// </summary>
    private void PopulateTileGrid(List<TileConfig> configs, Dictionary<string, HaEntityState> byId, HaClient client)
    {
        _lastConfigs = configs;
        ClearTileState();

        var grid = this.FindControl<Grid>("TileGrid")!;
        grid.ColumnDefinitions.Clear();
        for (var i = 0; i < _liveColumnCount; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(CellWidth, GridUnitType.Pixel));

        // Re-flows configs' list order into however many columns currently fit — never trusts the
        // stored Row/Col, which are the Settings tile editor's fixed 3-column layout, not this
        // resizable window's live one.
        var layoutConfigs = TileLayoutCompactor.Defragment(configs, _liveColumnCount);

        var maxRow = 0;
        var builtAny = false;

        foreach (var config in layoutConfigs)
        {
            var tile = config.Size == TileSize.Group
                ? BuildGroupTile(config, byId, client)
                : byId.TryGetValue(config.EntityId, out var state)
                    ? state.Domain switch
                    {
                        "cover" => BuildCoverTile(state, config, client),
                        "sensor" when config.IsGauge => BuildGaugeTile(state, config),
                        "sensor" => BuildSensorTile(state, config),
                        "camera" => BuildCameraTile(state, config, client),
                        "climate" => BuildClimateTile(state, config, client),
                        "lawn_mower" => BuildLawnMowerTile(state, config, client),
                        _ => BuildToggleTile(state, config, client),
                    }
                    : null;

            if (tile is null) continue; // entity vanished from HA (or an empty/orphaned group) since last selection

            _tileConfigsByEntityId[config.EntityId] = config;

            var rowSpan = TileLayoutCompactor.RowSpanFor(config.Size);
            Grid.SetRow(tile, config.Row);
            Grid.SetColumn(tile, config.Col);
            Grid.SetRowSpan(tile, rowSpan);
            Grid.SetColumnSpan(tile, TileLayoutCompactor.ColSpanFor(config.Size));
            grid.Children.Add(tile);

            maxRow = Math.Max(maxRow, config.Row + rowSpan);
            builtAny = true;
        }

        for (var i = 0; i < maxRow; i++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        if (!builtAny)
        {
            ShowEmptyState("🏠", Loc.Instance.Tr("Flyout.NoEntitiesTitle"), Loc.Instance.Tr("Flyout.NoEntitiesSubtitle"), showSettingsButton: false);
            return;
        }

        grid.IsVisible = true;
        this.FindControl<ContentControl>("EmptyStateHost")!.IsVisible = false;
    }

    private Control? BuildGroupTile(TileConfig config, Dictionary<string, HaEntityState> byId, HaClient client)
    {
        if (config.GroupEntityIds is not { Count: > 0 } ids) return null;

        var entities = new List<(string EntityId, HaEntityState State)>();
        foreach (var id in ids)
            if (byId.TryGetValue(id, out var state)) entities.Add((id, state));

        if (entities.Count == 0) return null;

        var tile = new GroupTile { GroupId = config.EntityId };
        tile.SetCornerRadius(AppSettings.Appearance.TileCornerRadius);
        tile.SetQuadrants(entities);
        tile.QuadrantActionRequested += async (_, entityId) =>
        {
            if (byId.TryGetValue(entityId, out var state))
                await PerformQuickActionAsync(client, state);
        };

        foreach (var id in ids)
            _groupTilesByChildEntityId[id] = tile;

        return tile;
    }

    /// <summary>The same toggle-or-cycle action a standalone tile's click performs, shared with each GroupTile quadrant's tap.</summary>
    private static async Task PerformQuickActionAsync(HaClient client, HaEntityState state)
    {
        try
        {
            if (state.Domain == "cover")
            {
                var isOpen = state.State is "open" or "opening";
                await client.CallServiceAsync("cover", isOpen ? "close_cover" : "open_cover", state.EntityId);
            }
            else
            {
                await client.ToggleAsync(state.EntityId);
            }
        }
        catch { /* best effort — tile resyncs from the next state_changed event */ }
    }

    private Control BuildToggleTile(HaEntityState state, TileConfig config, HaClient client)
    {
        var tile = new QuickToggleTile { EntityId = state.EntityId };
        tile.SetContent(
            config.CustomIcon ?? HaEntityDisplay.IconFor(state),
            config.CustomLabel ?? HaEntityDisplay.LabelFor(state),
            state.IsOn,
            HaEntityDisplay.LightColorFor(state));
        tile.SetCornerRadius(AppSettings.Appearance.TileCornerRadius);
        tile.SetSize(config.Size);
        tile.SetCustomColor(ParseColor(config.CustomColor));
        tile.Toggled += async (_, _) =>
        {
            try { await client.ToggleAsync(state.EntityId); }
            catch { /* tile will resync from the next state_changed event */ }
        };

        if (state.Domain == "light")
            tile.DetailRequested += (_, _) => LightDetailFlyout.Show(tile, state.EntityId, state, client);
        else if (state.Domain == "humidifier")
            tile.DetailRequested += (_, _) => HumidifierDetailFlyout.Show(tile, state.EntityId, state, client);

        _toggleTilesByEntityId[state.EntityId] = tile;
        return tile;
    }

    private Control BuildCoverTile(HaEntityState state, TileConfig config, HaClient client)
    {
        var tile = new CoverTile { EntityId = state.EntityId };
        tile.SetContent(state, config.CustomLabel ?? HaEntityDisplay.LabelFor(state));
        tile.SetCornerRadius(AppSettings.Appearance.TileCornerRadius);
        tile.SetSize(config.Size);
        tile.SetCustomColor(ParseColor(config.CustomColor));
        tile.OpenRequested += async (_, _) => await TryCallAsync(client, "cover", "open_cover", state.EntityId);
        tile.StopRequested += async (_, _) => await TryCallAsync(client, "cover", "stop_cover", state.EntityId);
        tile.CloseRequested += async (_, _) => await TryCallAsync(client, "cover", "close_cover", state.EntityId);

        _coverTilesByEntityId[state.EntityId] = tile;
        return tile;
    }

    private Control BuildSensorTile(HaEntityState state, TileConfig config)
    {
        var tile = new SensorTile { EntityId = state.EntityId };
        tile.SetContent(
            config.CustomIcon ?? HaEntityDisplay.IconFor(state),
            config.CustomLabel ?? HaEntityDisplay.LabelFor(state),
            HaEntityDisplay.ValueFor(state));
        tile.SetCornerRadius(AppSettings.Appearance.TileCornerRadius);
        tile.SetSize(config.Size);
        tile.SetCustomColor(ParseColor(config.CustomColor));

        _sensorTilesByEntityId[state.EntityId] = tile;
        return tile;
    }

    private Control BuildGaugeTile(HaEntityState state, TileConfig config)
    {
        var tile = new GaugeTile { EntityId = state.EntityId };
        tile.SetContent(state, config.CustomLabel ?? HaEntityDisplay.LabelFor(state));
        tile.SetCornerRadius(AppSettings.Appearance.TileCornerRadius);
        tile.SetSize(config.Size);
        tile.SetCustomColor(ParseColor(config.CustomColor));

        _gaugeTilesByEntityId[state.EntityId] = tile;
        return tile;
    }

    private Control BuildCameraTile(HaEntityState state, TileConfig config, HaClient client)
    {
        var tile = new CameraTile { EntityId = state.EntityId };
        tile.SetContent(config.CustomLabel ?? HaEntityDisplay.LabelFor(state), client);
        tile.SetCornerRadius(AppSettings.Appearance.TileCornerRadius);
        tile.SetSize(config.Size);
        tile.SetCustomColor(ParseColor(config.CustomColor));

        _cameraTilesByEntityId[state.EntityId] = tile;
        return tile;
    }

    private Control BuildClimateTile(HaEntityState state, TileConfig config, HaClient client)
    {
        var tile = new ClimateTile { EntityId = state.EntityId };
        tile.SetContent(state, config.CustomLabel ?? HaEntityDisplay.LabelFor(state));
        tile.SetCornerRadius(AppSettings.Appearance.TileCornerRadius);
        tile.SetSize(config.Size);
        tile.SetCustomColor(ParseColor(config.CustomColor));
        tile.ModeChangeRequested += async (_, mode) =>
            await TryCallAsync(client, "climate", "set_hvac_mode", state.EntityId, new System.Text.Json.Nodes.JsonObject { ["hvac_mode"] = mode });
        tile.DetailRequested += (_, _) => ThermostatDetailFlyout.Show(tile, state.EntityId, state, client);

        _climateTilesByEntityId[state.EntityId] = tile;
        return tile;
    }

    private Control BuildLawnMowerTile(HaEntityState state, TileConfig config, HaClient client)
    {
        var tile = new LawnMowerTile { EntityId = state.EntityId };
        tile.SetContent(state, config.CustomLabel ?? HaEntityDisplay.LabelFor(state));
        tile.SetCornerRadius(AppSettings.Appearance.TileCornerRadius);
        tile.SetSize(config.Size);
        tile.SetCustomColor(ParseColor(config.CustomColor));
        tile.StartRequested += async (_, _) => await TryCallAsync(client, "lawn_mower", "start_mowing", state.EntityId);
        tile.PauseRequested += async (_, _) => await TryCallAsync(client, "lawn_mower", "pause", state.EntityId);
        tile.DockRequested += async (_, _) => await TryCallAsync(client, "lawn_mower", "dock", state.EntityId);

        _lawnMowerTilesByEntityId[state.EntityId] = tile;
        return tile;
    }

    private void UpdateWeatherWidget(Dictionary<string, HaEntityState> byId, HaClient client)
    {
        var prefs = AppSettings.WeatherPrefs;
        var host = this.FindControl<ContentControl>("WeatherHost")!;

        if (!prefs.Enabled || prefs.EntityId is not { } entityId || !byId.TryGetValue(entityId, out var state))
        {
            HideWeatherWidget();
            return;
        }

        _weatherWidget.SetContent(state, client, prefs);
        host.Content = _weatherWidget;
        host.IsVisible = true;
    }

    private void HideWeatherWidget()
    {
        var host = this.FindControl<ContentControl>("WeatherHost")!;
        host.IsVisible = false;
        host.Content = null;
    }

    private void UpdateMediaPlayerWidget(Dictionary<string, HaEntityState> byId, HaClient client)
    {
        var prefs = AppSettings.MediaPlayerPrefs;
        var host = this.FindControl<ContentControl>("MediaPlayerHost")!;

        if (!prefs.Enabled || AppSettings.Credentials is not { } credentials
            || SelectMediaPlayerEntity(byId, prefs) is not { } state)
        {
            HideMediaPlayerWidget();
            return;
        }

        _currentMediaPlayerEntityId = state.EntityId;
        _mediaPlayerWidget.SetContent(state, client, credentials.ToConnectionSettings(), prefs.UseAlbumArtBackground);
        host.Content = _mediaPlayerWidget;
        host.IsVisible = true;
    }

    private void HideMediaPlayerWidget()
    {
        _currentMediaPlayerEntityId = null;
        var host = this.FindControl<ContentControl>("MediaPlayerHost")!;
        host.IsVisible = false;
        host.Content = null;
    }

    /// <summary>
    /// Picks which media_player to show. A configured EntityId always wins; otherwise auto-picks
    /// the best candidate (playing > paused > anything not off) so the widget works with zero setup,
    /// matching how Home Assistant's own media-control dashboard card behaves once an entity exists.
    /// Either way, an entity that reports no actual now-playing data (some Cast/browser sources only
    /// ever expose app_name — e.g. a bare "Chrome" entry with no title/artist/art) is treated as if
    /// nothing were playing, so the card doesn't show up with nothing useful in it.
    /// </summary>
    private static HaEntityState? SelectMediaPlayerEntity(Dictionary<string, HaEntityState> byId, MediaPlayerPreferences prefs)
    {
        if (prefs.EntityId is { } entityId)
            return byId.TryGetValue(entityId, out var configured) && HasNowPlayingData(configured) ? configured : null;

        HaEntityState? paused = null;
        HaEntityState? anyOn = null;
        foreach (var state in byId.Values)
        {
            if (state.Domain != "media_player" || !HasNowPlayingData(state)) continue;
            if (state.State == "playing") return state;
            if (state.State == "paused") paused ??= state;
            else if (state.State is not ("off" or "unavailable" or "unknown")) anyOn ??= state;
        }

        return paused ?? anyOn;
    }

    private static bool HasNowPlayingData(HaEntityState state) =>
        state.Attributes.ContainsKey("media_title") || state.Attributes.ContainsKey("media_artist") || state.Attributes.ContainsKey("entity_picture");

    private static async Task TryCallAsync(HaClient client, string domain, string service, string entityId, System.Text.Json.Nodes.JsonObject? extraData = null)
    {
        try { await client.CallServiceAsync(domain, service, entityId, extraData); }
        catch { /* best effort */ }
    }

    /// <summary>Parses a TileConfig.CustomColor hex string (e.g. "#3498DB"), or null if unset/malformed — a tile just keeps its default color in that case.</summary>
    private static Color? ParseColor(string? hex) =>
        hex is not null && Color.TryParse(hex, out var color) ? color : null;

    private Control BuildEmptyState(string icon, string title, string subtitle, bool showSettingsButton)
    {
        var panel = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = 280,
            Margin = new Thickness(0, 24, 0, 16),
        };

        panel.Children.Add(new TextBlock
        {
            Text = icon,
            FontSize = 28,
            FontFamily = "Segoe UI Emoji,Apple Color Emoji,Noto Color Emoji,Inter",
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        if (showSettingsButton)
        {
            var button = new Button
            {
                Content = Loc.Instance.Tr("Flyout.OpenSettings"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
                Classes = { "accent" },
            };
            button.Click += OnOpenSettingsClicked;
            panel.Children.Add(button);
        }

        return panel;
    }

    private void OnOpenSettingsClicked(object? sender, RoutedEventArgs e)
    {
        Hide();
        OpenSettingsRequested?.Invoke();
    }

    private void OnNotificationsButtonClicked(object? sender, RoutedEventArgs e)
    {
        NotificationHistoryFlyout.Show(this.FindControl<Button>("NotificationsButton")!, AppSettings.RecentNotifications);
    }

    private void OnEntityStateChanged(HaEntityState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (AppSettings.WeatherPrefs.Enabled && state.EntityId == AppSettings.WeatherPrefs.EntityId
                && AppSettings.Client is { } weatherClient)
                _weatherWidget.SetContent(state, weatherClient, AppSettings.WeatherPrefs);

            // Kept fresh for every entity (not just media_player) so a GroupTile's quadrants
            // for entities it isn't the one currently changing can still be rebuilt from
            // last-known state below, without a full GetStatesAsync round-trip.
            _lastKnownStates[state.EntityId] = state;

            if (AppSettings.MediaPlayerPrefs.Enabled && state.Domain == "media_player"
                && AppSettings.Client is { } client && AppSettings.Credentials is { } credentials)
            {
                // Re-run the same "pick the best player" logic a full refresh would use, so
                // playback starting on a different (or previously-idle) entity updates the
                // card immediately instead of only after the next full tile refresh.
                UpdateMediaPlayerWidget(_lastKnownStates, client);
                LayoutWidgetsRow(Width);
            }

            _tileConfigsByEntityId.TryGetValue(state.EntityId, out var config);
            config ??= new TileConfig(state.EntityId);

            if (_toggleTilesByEntityId.TryGetValue(state.EntityId, out var tile))
                tile.SetContent(config.CustomIcon ?? HaEntityDisplay.IconFor(state), config.CustomLabel ?? HaEntityDisplay.LabelFor(state), state.IsOn, HaEntityDisplay.LightColorFor(state));
            else if (_coverTilesByEntityId.TryGetValue(state.EntityId, out var coverTile))
                coverTile.SetContent(state, config.CustomLabel ?? HaEntityDisplay.LabelFor(state));
            else if (_sensorTilesByEntityId.TryGetValue(state.EntityId, out var sensorTile))
                sensorTile.SetContent(config.CustomIcon ?? HaEntityDisplay.IconFor(state), config.CustomLabel ?? HaEntityDisplay.LabelFor(state), HaEntityDisplay.ValueFor(state));
            else if (_gaugeTilesByEntityId.TryGetValue(state.EntityId, out var gaugeTile))
                gaugeTile.SetContent(state, config.CustomLabel ?? HaEntityDisplay.LabelFor(state));
            else if (_climateTilesByEntityId.TryGetValue(state.EntityId, out var climateTile))
                climateTile.SetContent(state, config.CustomLabel ?? HaEntityDisplay.LabelFor(state));
            else if (_lawnMowerTilesByEntityId.TryGetValue(state.EntityId, out var lawnMowerTile))
                lawnMowerTile.SetContent(state, config.CustomLabel ?? HaEntityDisplay.LabelFor(state));
            else if (_groupTilesByChildEntityId.TryGetValue(state.EntityId, out var groupTile))
                RefreshGroupQuadrants(groupTile);
        });
    }

    private void RefreshGroupQuadrants(GroupTile groupTile)
    {
        if (groupTile.GroupId is null) return;
        if (!_tileConfigsByEntityId.TryGetValue(groupTile.GroupId, out var groupConfig)) return;
        if (groupConfig.GroupEntityIds is not { } ids) return;

        var entities = new List<(string EntityId, HaEntityState State)>();
        foreach (var id in ids)
            if (_lastKnownStates.TryGetValue(id, out var s)) entities.Add((id, s));

        groupTile.SetQuadrants(entities);
    }

    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        PositionNearTray();
        Show();
        Activate();
    }

    private void PositionNearTray()
    {
        var screen = Screens.Primary ?? Screens.All[0];
        var workArea = screen.WorkingArea;

        // WorkingArea/Position are physical pixels, but Width/Height are logical
        // (DIP) units — convert through the screen's scaling or this ends up
        // off-screen on any display above 100% scale.
        var scaling = screen.Scaling;
        var pixelWidth = (int)(Width * scaling);
        var pixelHeight = (int)(Height * scaling);
        var marginPx = (int)(12 * scaling);

        // Re-evaluated on every open, not just once at startup — the primary screen (or its
        // taskbar/panel position) can change between sessions, and ApplyAnchorCorner has to stay
        // in sync with wherever this actually ends up anchored so the earlier resize-handle fix
        // still protects the right corner.
        var corner = DetermineAnchorCorner(screen);
        ApplyAnchorCorner(corner);

        var x = corner is AnchorCorner.BottomRight or AnchorCorner.TopRight
            ? workArea.X + workArea.Width - pixelWidth - marginPx
            : workArea.X + marginPx;

        var y = corner is AnchorCorner.BottomRight or AnchorCorner.BottomLeft
            ? workArea.Y + workArea.Height - pixelHeight - marginPx
            : workArea.Y + marginPx;

        Position = new PixelPoint(x, y);
    }
}
