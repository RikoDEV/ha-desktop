using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>
/// Read-only camera tile: a periodically-refreshed still snapshot (not a live MJPEG/WebRTC
/// stream — polling a still frame every few seconds keeps this in line with the app's
/// low-CPU/low-memory goal). Clicking opens a larger view with a faster refresh cadence.
/// </summary>
public partial class CameraTile : UserControl
{
    private static readonly TimeSpan TileRefreshInterval = TimeSpan.FromSeconds(10);

    public string? EntityId { get; set; }

    private HaClient? _client;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TileRefreshInterval };
    private int _refreshToken;

    public CameraTile()
    {
        InitializeComponent();
        this.FindControl<PathIcon>("OfflineIcon")!.Data = Geometry.Parse(TileIcons.PathFor("camera"));
        _refreshTimer.Tick += (_, _) => _ = RefreshSnapshotAsync();
        DetachedFromVisualTree += (_, _) => _refreshTimer.Stop();
        this.FindControl<Border>("RootBorder")!.PointerPressed += OnPointerPressed;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void SetContent(string label, HaClient client)
    {
        _client = client;
        this.FindControl<TextBlock>("LabelText")!.Text = label;
        _ = RefreshSnapshotAsync();
        _refreshTimer.Start();
    }

    public void SetCornerRadius(double radius) =>
        this.FindControl<Border>("RootBorder")!.CornerRadius = new Avalonia.CornerRadius(radius);

    public void SetSize(TileSize size) => Width = size == TileSize.Wide ? 184 : 88;

    private async Task RefreshSnapshotAsync()
    {
        if (_client is null || EntityId is null) return;

        // A dropped/reconnecting client means the access token backing this REST call may already
        // be stale — polling through that anyway hammers Home Assistant's camera_proxy endpoint
        // with a bad token every tick until reconnect finishes, which is exactly the pattern that
        // trips HA's own IP-ban-after-N-failed-logins protection (this has happened: HA banned the
        // machine's IP overnight after the tile kept polling through an expired token). Skipping
        // the call while disconnected is cheap insurance — the next successful tick after
        // reconnect just resumes normally.
        if (_client.ConnectionState != HaConnectionState.Connected) return;

        var myToken = ++_refreshToken;
        var bytes = await _client.GetCameraSnapshotAsync(EntityId);
        if (myToken != _refreshToken) return; // superseded by a newer tick or a rebuilt tile

        var offlineIcon = this.FindControl<PathIcon>("OfflineIcon")!;
        if (bytes is null)
        {
            offlineIcon.IsVisible = this.FindControl<Image>("SnapshotImage")!.Source is null;
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            this.FindControl<Image>("SnapshotImage")!.Source = bitmap;
            offlineIcon.IsVisible = false;
        }
        catch
        {
            // corrupt/partial frame — keep whatever was last shown rather than blank the tile
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_client is null || EntityId is null) return;
        CameraDetailFlyout.Show(this, EntityId, _client);
    }
}
