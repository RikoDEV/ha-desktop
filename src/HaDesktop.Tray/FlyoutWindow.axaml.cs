using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using HaDesktop.Core.Diagnostics;
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
    /// <summary>A tile currently in the grid, with the configuration it was built for — a tile is reused across refreshes for as long as that hasn't changed.</summary>
    private sealed record TileEntry(Control Control, TileConfig Config, double CornerRadius);

    // Keyed by TileConfig.EntityId — a real entity id, or a group tile's synthetic "group:" id.
    private readonly Dictionary<string, TileEntry> _tiles = new();
    // Keyed by real entity id, which is all a state_changed event ever names: a group tile is
    // listed here once per member.
    private readonly Dictionary<string, IEntityTile> _tilesByEntityId = new();
    private readonly WeatherWidget _weatherWidget = new();
    private readonly MediaPlayerWidget _mediaPlayerWidget = new();

    private static readonly IReadOnlyDictionary<string, HaEntityState> NoStates = new Dictionary<string, HaEntityState>();

    /// <summary>Whichever client the states on screen came from — so its StateChanged handler can be detached once the session moves to another.</summary>
    private HaClient? _subscribedClient;

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
        SettingsIcon.Data = TileIcons.GeometryFor("cog");
        NotificationsIcon.Data = TileIcons.GeometryFor("bell");
        _liveColumnCount = ComputeColumnCount(Width);
        Deactivated += (_, _) => Hide();
        AppSettings.ConnectionChanged += OnConnectionChanged;
        AppSettings.LocalPreferencesLoaded += OnLocalPreferencesLoaded;
        Loc.Instance.LanguageChanged += OnConnectionChanged;
        Resized += OnResized;
        AttachResizeHandlers();
        _ = RefreshTilesAsync();
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
    /// WindowDecorations="None" drops the OS's own resizable border along with its chrome, and
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
            if (_lastConfigs.Count > 0 && HaSession.Client is { } client)
                PopulateTileGrid(_lastConfigs, client.States);
        });
    }

    private void OnResized(object? sender, WindowResizedEventArgs e)
    {
        LayoutWidgetsRow(e.ClientSize.Width);

        var newColumnCount = ComputeColumnCount(e.ClientSize.Width);
        if (newColumnCount != _liveColumnCount && _lastConfigs.Count > 0 && HaSession.Client is { } client)
        {
            _liveColumnCount = newColumnCount;
            PopulateTileGrid(_lastConfigs, client.States);
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
        var weatherHost = WeatherHost;
        var mediaHost = MediaPlayerHost;
        var widgetsGrid = WidgetsGrid;

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

    private void ClearTiles()
    {
        foreach (var entry in _tiles.Values)
            (entry.Control as IDisposable)?.Dispose();

        TileGrid.Children.Clear();
        TileGrid.RowDefinitions.Clear();
        _tiles.Clear();
        _tilesByEntityId.Clear();
        _lastConfigs = new();
    }

    private void ShowEmptyState(string icon, string title, string subtitle, bool showSettingsButton)
    {
        TileGrid.IsVisible = false;
        EmptyStateHost.Content = BuildEmptyState(icon, title, subtitle, showSettingsButton);
        EmptyStateHost.IsVisible = true;
    }

    private void ShowDisconnected(string icon, string titleKey, string subtitleKey)
    {
        ClearTiles();
        HideWeatherWidget();
        HideMediaPlayerWidget();
        ShowEmptyState(icon, Loc.Instance.Tr(titleKey), Loc.Instance.Tr(subtitleKey), showSettingsButton: true);
    }

    private async Task RefreshTilesAsync()
    {
        // AppSettings.ConnectionChanged fires for many unrelated reasons (any settings
        // change, reconnects, etc.) and this method can await a network call, so overlapping
        // calls are routine — only the newest one gets to touch the grid.
        var myToken = ++_tilesRefreshToken;

        var client = HaSession.Client;
        if (!ReferenceEquals(client, _subscribedClient))
        {
            if (_subscribedClient is not null) _subscribedClient.StateChanged -= OnEntityStateChanged;
            if (client is not null) client.StateChanged += OnEntityStateChanged;
            _subscribedClient = client;
        }

        if (client is null)
        {
            ShowDisconnected("🔌", "Flyout.NotConnectedTitle", "Flyout.NotConnectedSubtitle");
            return;
        }

        // The session loads states as part of connecting, so this is normally already done and
        // everything below is synchronous — it only has to fetch here if that load failed.
        if (!client.StatesLoaded)
        {
            try
            {
                await client.RefreshStatesAsync();
            }
            catch (Exception ex)
            {
                Log.Swallowed(ex);
                if (myToken == _tilesRefreshToken)
                    ShowDisconnected("⚠", "Flyout.ConnectionErrorTitle", "Flyout.ConnectionErrorSubtitle");
                return;
            }

            if (myToken != _tilesRefreshToken) return; // superseded by a later call while we were awaiting
        }

        var states = client.States;
        UpdateWeatherWidget(states);
        UpdateMediaPlayerWidget(states);
        LayoutWidgetsRow(Width);

        List<TileConfig> configs = AppSettings.SelectedTiles.Count > 0
            // User has picked specific tiles in Settings — show exactly those, at their chosen positions/sizes.
            ? AppSettings.SelectedTiles
            // No selection yet — fall back to a reasonable default so the flyout isn't empty on first connect.
            // Not yet positioned (fresh, ephemeral list), so compacted the same way a persisted list would be.
            : TileLayoutCompactor.Compact(states.Keys.Where(AppSettings.IsDefaultTileDomain).Order(StringComparer.Ordinal).Take(8).Select(id => new TileConfig(id)).ToList());

        PopulateTileGrid(configs, states);
    }

    /// <summary>
    /// Lays every tile in <paramref name="configs"/> out in TileGrid. Called both after a
    /// RefreshTilesAsync and from a resize that changed how many columns currently fit. A tile
    /// already on screen is kept (and just moved) unless its configuration changed — so a
    /// reconnect, a language switch or an unrelated settings change doesn't tear down and rebuild
    /// every control in the flyout.
    /// </summary>
    private void PopulateTileGrid(List<TileConfig> configs, IReadOnlyDictionary<string, HaEntityState> states)
    {
        _lastConfigs = configs;

        TileGrid.ColumnDefinitions.Clear();
        for (var i = 0; i < _liveColumnCount; i++)
            TileGrid.ColumnDefinitions.Add(new ColumnDefinition(CellWidth, GridUnitType.Pixel));

        // Re-flows configs' list order into however many columns currently fit — never trusts the
        // stored Row/Col, which are the Settings tile editor's fixed 3-column layout, not this
        // resizable window's live one.
        var layoutConfigs = TileLayoutCompactor.Defragment(configs, _liveColumnCount);
        var cornerRadius = AppSettings.Appearance.TileCornerRadius;

        var previous = new Dictionary<string, TileEntry>(_tiles);
        _tiles.Clear();
        _tilesByEntityId.Clear();

        var maxRow = 0;
        foreach (var config in layoutConfigs)
        {
            var memberIds = config.Size == TileSize.Group ? config.GroupEntityIds : new List<string> { config.EntityId };
            var state = memberIds?.Select(id => states.GetValueOrDefault(id)).FirstOrDefault(member => member is not null);
            if (memberIds is null || state is null) continue; // entity vanished from HA (or an empty/orphaned group) since last selection

            // Position is applied separately below, so it's left out of what counts as "changed".
            var identity = config with { Row = -1, Col = -1 };
            Control control;
            if (previous.TryGetValue(config.EntityId, out var existing) && existing.Config == identity && existing.CornerRadius == cornerRadius)
            {
                control = existing.Control;
                previous.Remove(config.EntityId);
            }
            else
            {
                control = CreateTile(config, state);
                ((IEntityTile)control).Configure(config, cornerRadius);
                TileGrid.Children.Add(control);
            }

            var tile = (IEntityTile)control;
            tile.Update(state);
            _tiles[config.EntityId] = new TileEntry(control, identity, cornerRadius);
            foreach (var id in memberIds) _tilesByEntityId[id] = tile;

            var rowSpan = TileLayoutCompactor.RowSpanFor(config.Size);
            Grid.SetRow(control, config.Row);
            Grid.SetColumn(control, config.Col);
            Grid.SetRowSpan(control, rowSpan);
            Grid.SetColumnSpan(control, TileLayoutCompactor.ColSpanFor(config.Size));

            maxRow = Math.Max(maxRow, config.Row + rowSpan);
        }

        foreach (var stale in previous.Values)
        {
            TileGrid.Children.Remove(stale.Control);
            (stale.Control as IDisposable)?.Dispose();
        }

        TileGrid.RowDefinitions.Clear();
        for (var i = 0; i < maxRow; i++)
            TileGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        if (_tiles.Count == 0)
        {
            ShowEmptyState("🏠", Loc.Instance.Tr("Flyout.NoEntitiesTitle"), Loc.Instance.Tr("Flyout.NoEntitiesSubtitle"), showSettingsButton: false);
            return;
        }

        TileGrid.IsVisible = true;
        EmptyStateHost.IsVisible = false;
    }

    private static Control CreateTile(TileConfig config, HaEntityState state)
    {
        if (config.Size == TileSize.Group) return new GroupTile();

        return state.Domain switch
        {
            "cover" => new CoverTile(),
            "sensor" when config.IsGauge => new GaugeTile(),
            "sensor" => new SensorTile(),
            "camera" => new CameraTile(),
            "climate" => new ClimateTile(),
            "lawn_mower" => new LawnMowerTile(),
            _ => new QuickToggleTile(),
        };
    }

    private void UpdateWeatherWidget(IReadOnlyDictionary<string, HaEntityState> states)
    {
        var prefs = AppSettings.WeatherPrefs;

        if (!prefs.Enabled || prefs.EntityId is not { } entityId || !states.TryGetValue(entityId, out var state))
        {
            HideWeatherWidget();
            return;
        }

        _weatherWidget.SetContent(state, prefs);
        WeatherHost.Content = _weatherWidget;
        WeatherHost.IsVisible = true;
    }

    private void HideWeatherWidget()
    {
        WeatherHost.IsVisible = false;
        WeatherHost.Content = null;
    }

    private void UpdateMediaPlayerWidget(IReadOnlyDictionary<string, HaEntityState> states)
    {
        var prefs = AppSettings.MediaPlayerPrefs;

        if (!prefs.Enabled || SelectMediaPlayerEntity(states, prefs) is not { } state)
        {
            HideMediaPlayerWidget();
            return;
        }

        _mediaPlayerWidget.SetContent(state, prefs.UseAlbumArtBackground);
        MediaPlayerHost.Content = _mediaPlayerWidget;
        MediaPlayerHost.IsVisible = true;
    }

    private void HideMediaPlayerWidget()
    {
        MediaPlayerHost.IsVisible = false;
        MediaPlayerHost.Content = null;
    }

    /// <summary>
    /// Picks which media_player to show. A configured EntityId always wins; otherwise auto-picks
    /// the best candidate (playing > paused > anything not off) so the widget works with zero setup,
    /// matching how Home Assistant's own media-control dashboard card behaves once an entity exists.
    /// Either way, an entity that reports no actual now-playing data (some Cast/browser sources only
    /// ever expose app_name — e.g. a bare "Chrome" entry with no title/artist/art) is treated as if
    /// nothing were playing, so the card doesn't show up with nothing useful in it.
    /// </summary>
    private static HaEntityState? SelectMediaPlayerEntity(IReadOnlyDictionary<string, HaEntityState> byId, MediaPlayerPreferences prefs)
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
        NotificationHistoryFlyout.Show(NotificationsButton, NotificationRelay.RecentNotifications);
    }

    /// <summary>Raised on the client's receive thread, and only for entities the session tracks — see <see cref="AppSettings.BuildStateFilter"/>.</summary>
    private void OnEntityStateChanged(HaEntityState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (HaSession.Client is not { } client || !ReferenceEquals(client, _subscribedClient)) return;

            if (AppSettings.WeatherPrefs.Enabled && state.EntityId == AppSettings.WeatherPrefs.EntityId)
                _weatherWidget.SetContent(state, AppSettings.WeatherPrefs);

            if (AppSettings.MediaPlayerPrefs.Enabled && state.Domain == "media_player")
            {
                // Re-run the same "pick the best player" logic a full refresh would use, so
                // playback starting on a different (or previously-idle) entity updates the
                // card immediately instead of only after the next full tile refresh.
                UpdateMediaPlayerWidget(client.States);
                LayoutWidgetsRow(Width);
            }

            if (_tilesByEntityId.TryGetValue(state.EntityId, out var tile))
                tile.Update(state);
        });
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
