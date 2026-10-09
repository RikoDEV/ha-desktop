using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using HaDesktop.Core.Ha;

namespace HaDesktop.Tray;

/// <summary>Click-through detail popup for a camera tile: a larger snapshot refreshed every couple of seconds while open.</summary>
public static class CameraDetailFlyout
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    public static void Show(Control anchor, string entityId)
    {
        var image = new Image
        {
            Width = 320,
            Height = 180,
            Stretch = Avalonia.Media.Stretch.Uniform,
        };

        var isOpen = true;

        async Task RefreshAsync()
        {
            // See CameraTile.RefreshSnapshotAsync — skip while disconnected/reconnecting rather
            // than hammering camera_proxy with a possibly-stale token every 2 seconds.
            if (HaSession.Client is not { ConnectionState: HaConnectionState.Connected } client) return;

            var bytes = await client.GetCameraSnapshotAsync(entityId);
            if (bytes is null || !isOpen) return;

            var bitmap = await CameraSnapshot.DecodeAsync(bytes, anchor, image.Width);
            if (bitmap is null) return; // corrupt/partial frame — keep the previous one visible

            if (isOpen) CameraSnapshot.Replace(image, bitmap);
            else bitmap.Dispose();
        }

        var timer = new DispatcherTimer { Interval = RefreshInterval };
        timer.Tick += (_, _) => _ = RefreshAsync();

        var content = new Border
        {
            CornerRadius = new Avalonia.CornerRadius(6),
            ClipToBounds = true,
            Child = image,
        };

        var flyout = DetailFlyoutControls.Show(anchor, content);
        flyout.Closed += (_, _) =>
        {
            isOpen = false;
            timer.Stop();
            CameraSnapshot.Replace(image, null);
        };

        // DispatcherTimer waits a full interval before its first tick, so fetch one frame immediately too.
        timer.Start();
        _ = RefreshAsync();
    }
}
