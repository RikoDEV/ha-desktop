using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HaDesktop.Core.Diagnostics;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>
/// Read-only camera tile: a periodically-refreshed still snapshot (not a live MJPEG/WebRTC
/// stream — polling a still frame every few seconds keeps this in line with the app's
/// low-CPU/low-memory goal). Clicking opens a larger view with a faster refresh cadence.
/// Only polls while the flyout is actually open.
/// </summary>
public partial class CameraTile : UserControl, IEntityTile, IDisposable
{
    private static readonly TimeSpan TileRefreshInterval = TimeSpan.FromSeconds(10);

    private TileConfig? _config;
    private string? _entityId;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TileRefreshInterval };
    private int _refreshToken;
    private bool _isOnScreen;

    public CameraTile()
    {
        InitializeComponent();
        OfflineIcon.Data = TileIcons.GeometryFor("camera");
        _refreshTimer.Tick += (_, _) => _ = RefreshSnapshotAsync();
        RootBorder.PointerPressed += OnPointerPressed;
        WindowVisibility.Track(this, OnScreenChanged);
    }

    public void Configure(TileConfig config, double cornerRadius)
    {
        _config = config;
        RootBorder.CornerRadius = new Avalonia.CornerRadius(cornerRadius);
        // Mostly only visible behind the offline icon, since a live snapshot otherwise covers the whole tile.
        if (TileDimensions.CustomBrushFor(config) is { } brush) RootBorder.Background = brush;
        this.SetTileSize(config.Size);
    }

    public void Update(HaEntityState state)
    {
        var isFirstUpdate = _entityId is null;
        _entityId = state.EntityId;
        LabelText.Text = _config?.CustomLabel ?? HaEntityDisplay.LabelFor(state);

        if (isFirstUpdate && _isOnScreen) _ = RefreshSnapshotAsync();
    }

    /// <summary>A hidden flyout has nobody looking at it: stop fetching and decoding frames until it's opened again, then show a fresh one straight away.</summary>
    private void OnScreenChanged(bool isOnScreen)
    {
        _isOnScreen = isOnScreen;
        if (isOnScreen)
        {
            _ = RefreshSnapshotAsync();
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
            _refreshToken++; // drop whatever fetch is still in flight
        }
    }

    private async Task RefreshSnapshotAsync()
    {
        if (_entityId is null || HaSession.Client is not { } client) return;

        // A dropped/reconnecting/disposed client means the access token backing this REST call may
        // already be stale — polling through that anyway hammers Home Assistant's camera_proxy
        // endpoint with a bad token every tick, which is exactly the pattern that trips HA's own
        // IP-ban-after-N-failed-logins protection (this has happened: HA banned the machine's IP
        // overnight after the tile kept polling through an expired token). HaClient latches a 401
        // as a second line of defence; the next successful tick after reconnect just resumes normally.
        if (client.ConnectionState != HaConnectionState.Connected) return;

        var myToken = ++_refreshToken;
        var bytes = await client.GetCameraSnapshotAsync(_entityId);
        if (myToken != _refreshToken) return; // superseded by a newer tick, or the flyout was hidden meanwhile

        if (bytes is null)
        {
            OfflineIcon.IsVisible = SnapshotImage.Source is null;
            return;
        }

        var bitmap = await CameraSnapshot.DecodeAsync(bytes, this, Math.Max(Width, Height * 2));
        if (bitmap is null) return; // corrupt/partial frame — keep whatever was last shown rather than blank the tile

        if (myToken != _refreshToken)
        {
            bitmap.Dispose();
            return;
        }

        CameraSnapshot.Replace(SnapshotImage, bitmap);
        OfflineIcon.IsVisible = false;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_entityId is not null) CameraDetailFlyout.Show(this, _entityId);
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _refreshToken++;
        CameraSnapshot.Replace(SnapshotImage, null);
    }
}

/// <summary>Decoding and swapping camera frames without holding on to more pixels than are shown.</summary>
internal static class CameraSnapshot
{
    /// <summary>
    /// Decodes a frame scaled down to the size it will be displayed at — a 1080p frame is about
    /// 8 MB of pixels, a tile-sized one a few dozen kilobytes. Decoded off the UI thread. Null if
    /// the bytes aren't a readable image.
    /// </summary>
    public static Task<Bitmap?> DecodeAsync(byte[] bytes, Control target, double logicalWidth)
    {
        var scaling = TopLevel.GetTopLevel(target)?.RenderScaling ?? 1;
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(logicalWidth * scaling));

        return Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(stream, pixelWidth);
            }
            catch (Exception ex)
            {
                Log.Swallowed(ex);
                return null;
            }
        });
    }

    /// <summary>
    /// Shows <paramref name="bitmap"/> and frees the one it replaces right away. A bitmap's pixels
    /// live outside the managed heap, so the GC sees no pressure to finalize a dropped one — left
    /// to it, every refreshed frame's memory lingered.
    /// </summary>
    public static void Replace(Image image, Bitmap? bitmap)
    {
        var previous = image.Source as Bitmap;
        image.Source = bitmap;
        previous?.Dispose();
    }
}
