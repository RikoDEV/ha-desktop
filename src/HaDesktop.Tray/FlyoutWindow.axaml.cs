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
    // MDI "cog" (Material Design Icons, Apache-2.0) — vector, no font dependency.
    private const string GearIconPath =
        "M12,15.5A3.5,3.5 0 0,1 8.5,12A3.5,3.5 0 0,1 12,8.5A3.5,3.5 0 0,1 15.5,12A3.5,3.5 0 0,1 12,15.5M19.43,12.97C19.47,12.65 19.5,12.33 19.5,12C19.5,11.67 19.47,11.34 19.43,11L21.54,9.37C21.73,9.22 21.78,8.95 21.66,8.73L19.66,5.27C19.54,5.05 19.27,4.96 19.05,5.05L16.56,6.05C16.04,5.66 15.5,5.32 14.87,5.07L14.5,2.42C14.46,2.18 14.25,2 14,2H10C9.75,2 9.54,2.18 9.5,2.42L9.13,5.07C8.5,5.32 7.96,5.66 7.44,6.05L4.95,5.05C4.72,4.96 4.46,5.05 4.34,5.27L2.34,8.73C2.21,8.95 2.27,9.22 2.46,9.37L4.57,11C4.53,11.34 4.5,11.67 4.5,12C4.5,12.33 4.53,12.65 4.57,12.97L2.46,14.63C2.27,14.78 2.21,15.05 2.34,15.27L4.34,18.73C4.46,18.95 4.72,19.03 4.95,18.95L7.44,17.94C7.96,18.34 8.5,18.68 9.13,18.93L9.5,21.58C9.54,21.82 9.75,22 10,22H14C14.25,22 14.46,21.82 14.5,21.58L14.87,18.93C15.5,18.67 16.04,18.34 16.56,17.94L19.05,18.95C19.27,19.03 19.54,18.95 19.66,18.73L21.66,15.27C21.78,15.05 21.73,14.78 21.54,14.63L19.43,12.97Z";

    // MDI "bell" (Material Design Icons, Apache-2.0) — vector, no font dependency.
    private const string BellIconPath =
        "M21,19V20H3V19L5,17V11C5,7.9 7.03,5.17 10,4.29C10,4.19 10,4.1 10,4A2,2 0 0,1 12,2A2,2 0 0,1 14,4C14,4.1 14,4.19 14,4.29C16.97,5.17 19,7.9 19,11V17L21,19M14,21A2,2 0 0,1 12,23A2,2 0 0,1 10,21H14Z";

    private readonly Dictionary<string, QuickToggleTile> _toggleTilesByEntityId = new();
    private readonly Dictionary<string, CoverTile> _coverTilesByEntityId = new();
    private readonly Dictionary<string, SensorTile> _sensorTilesByEntityId = new();
    private readonly Dictionary<string, CameraTile> _cameraTilesByEntityId = new();
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

    /// <summary>Raised when the user asks to open Settings from within the flyout (e.g. the "not connected" state or the header button).</summary>
    public event Action? OpenSettingsRequested;

    public FlyoutWindow()
    {
        InitializeComponent();
        this.FindControl<PathIcon>("SettingsIcon")!.Data = Geometry.Parse(GearIconPath);
        this.FindControl<PathIcon>("NotificationsIcon")!.Data = Geometry.Parse(BellIconPath);
        Deactivated += (_, _) => Hide();
        AppSettings.ConnectionChanged += OnConnectionChanged;
        Loc.Instance.LanguageChanged += OnConnectionChanged;
        _ = RefreshTilesAsync();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnConnectionChanged()
    {
        Dispatcher.UIThread.Post(() => _ = RefreshTilesAsync());
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
        _cameraTilesByEntityId.Clear();
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

        List<TileConfig> configs = AppSettings.SelectedTiles.Count > 0
            // User has picked specific tiles in Settings — show exactly those, at their chosen positions/sizes.
            ? AppSettings.SelectedTiles
            // No selection yet — fall back to a reasonable default so the flyout isn't empty on first connect.
            // Not yet positioned (fresh, ephemeral list), so compacted the same way a persisted list would be.
            : TileLayoutCompactor.Compact(states.Where(s => s.Domain is "light" or "switch" or "cover").Take(8).Select(s => new TileConfig(s.EntityId)).ToList());

        var grid = this.FindControl<Grid>("TileGrid")!;
        var maxRow = 0;
        var builtAny = false;

        foreach (var config in configs)
        {
            var tile = config.Size == TileSize.Group
                ? BuildGroupTile(config, byId, client)
                : byId.TryGetValue(config.EntityId, out var state)
                    ? state.Domain switch
                    {
                        "cover" => BuildCoverTile(state, config, client),
                        "sensor" => BuildSensorTile(state, config),
                        "camera" => BuildCameraTile(state, config, client),
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
        tile.Toggled += async (_, _) =>
        {
            try { await client.ToggleAsync(state.EntityId); }
            catch { /* tile will resync from the next state_changed event */ }
        };

        if (state.Domain == "light")
            tile.DetailRequested += (_, _) => LightDetailFlyout.Show(tile, state.EntityId, state, client);

        _toggleTilesByEntityId[state.EntityId] = tile;
        return tile;
    }

    private Control BuildCoverTile(HaEntityState state, TileConfig config, HaClient client)
    {
        var tile = new CoverTile { EntityId = state.EntityId };
        tile.SetContent(state, config.CustomLabel ?? HaEntityDisplay.LabelFor(state));
        tile.SetCornerRadius(AppSettings.Appearance.TileCornerRadius);
        tile.SetSize(config.Size);
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

        _sensorTilesByEntityId[state.EntityId] = tile;
        return tile;
    }

    private Control BuildCameraTile(HaEntityState state, TileConfig config, HaClient client)
    {
        var tile = new CameraTile { EntityId = state.EntityId };
        tile.SetContent(config.CustomLabel ?? HaEntityDisplay.LabelFor(state), client);
        tile.SetCornerRadius(AppSettings.Appearance.TileCornerRadius);
        tile.SetSize(config.Size);

        _cameraTilesByEntityId[state.EntityId] = tile;
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

    private static async Task TryCallAsync(HaClient client, string domain, string service, string entityId)
    {
        try { await client.CallServiceAsync(domain, service, entityId); }
        catch { /* best effort */ }
    }

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
            }

            _tileConfigsByEntityId.TryGetValue(state.EntityId, out var config);
            config ??= new TileConfig(state.EntityId);

            if (_toggleTilesByEntityId.TryGetValue(state.EntityId, out var tile))
                tile.SetContent(config.CustomIcon ?? HaEntityDisplay.IconFor(state), config.CustomLabel ?? HaEntityDisplay.LabelFor(state), state.IsOn, HaEntityDisplay.LightColorFor(state));
            else if (_coverTilesByEntityId.TryGetValue(state.EntityId, out var coverTile))
                coverTile.SetContent(state, config.CustomLabel ?? HaEntityDisplay.LabelFor(state));
            else if (_sensorTilesByEntityId.TryGetValue(state.EntityId, out var sensorTile))
                sensorTile.SetContent(config.CustomIcon ?? HaEntityDisplay.IconFor(state), config.CustomLabel ?? HaEntityDisplay.LabelFor(state), HaEntityDisplay.ValueFor(state));
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
        const int margin = 12;

        // Anchor bottom-right, matching the typical Windows/Linux tray flyout
        // position. macOS overrides this later to anchor top-right under the
        // menu-bar item instead.
        Position = new PixelPoint(
            workArea.X + workArea.Width - pixelWidth - (int)(margin * scaling),
            workArea.Y + workArea.Height - pixelHeight - (int)(margin * scaling));
    }
}
