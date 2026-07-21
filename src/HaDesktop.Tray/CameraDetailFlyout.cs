using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HaDesktop.Core.Ha;

namespace HaDesktop.Tray;

/// <summary>Click-through detail popup for a camera tile: a larger snapshot refreshed every couple of seconds while open.</summary>
public static class CameraDetailFlyout
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    public static void Show(Control anchor, string entityId, HaClient client)
    {
        var image = new Image
        {
            Width = 320,
            Height = 180,
            Stretch = Avalonia.Media.Stretch.Uniform,
        };

        var timer = new DispatcherTimer { Interval = RefreshInterval };
        timer.Tick += async (_, _) =>
        {
            // See CameraTile.RefreshSnapshotAsync — skip while disconnected/reconnecting rather
            // than hammering camera_proxy with a possibly-stale token every 2 seconds.
            if (client.ConnectionState != HaConnectionState.Connected) return;

            var bytes = await client.GetCameraSnapshotAsync(entityId);
            if (bytes is null) return;
            try
            {
                using var stream = new MemoryStream(bytes);
                image.Source = new Bitmap(stream);
            }
            catch { /* corrupt/partial frame — keep the previous one visible */ }
        };

        var content = new Border
        {
            CornerRadius = new Avalonia.CornerRadius(6),
            ClipToBounds = true,
            Child = image,
        };

        var flyout = new Flyout { Content = content, Placement = PlacementMode.Bottom };
        flyout.Closed += (_, _) => timer.Stop();
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);

        // DispatcherTimer waits a full interval before its first tick, so fetch one frame immediately too.
        timer.Start();
        _ = RefreshOnceAsync();

        async System.Threading.Tasks.Task RefreshOnceAsync()
        {
            if (client.ConnectionState != HaConnectionState.Connected) return;

            var bytes = await client.GetCameraSnapshotAsync(entityId);
            if (bytes is null) return;
            try
            {
                using var stream = new MemoryStream(bytes);
                image.Source = new Bitmap(stream);
            }
            catch { /* corrupt/partial frame */ }
        }
    }
}
