using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HaDesktop.Core.Diagnostics;
using HaDesktop.Core.Ha;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

/// <summary>
/// Mirrors Home Assistant's own "media-control" dashboard card: album art, title/artist/source,
/// power toggle, shuffle/prev/play-pause/next/repeat, and a volume slider — each control shown
/// only when the entity's supported_features bitmask reports it.
/// </summary>
public partial class MediaPlayerWidget : UserControl
{
    // media_player.MediaPlayerEntityFeature bit flags (Home Assistant core).
    [Flags]
    private enum Feature
    {
        Pause = 1,
        Seek = 2,
        VolumeSet = 4,
        VolumeMute = 8,
        PreviousTrack = 16,
        NextTrack = 32,
        TurnOn = 128,
        TurnOff = 256,
        PlayMedia = 512,
        VolumeStep = 1024,
        SelectSource = 2048,
        Stop = 4096,
        Shuffle = 32768,
        Play = 16384,
        Repeat = 262144,
    }

    // MDI icons (Material Design Icons, Apache-2.0) — vector, no font dependency.
    private static readonly Geometry PlayIcon = Geometry.Parse("M8,5.14V19.14L19,12.14L8,5.14Z");
    private static readonly Geometry PauseIcon = Geometry.Parse("M14,19H18V5H14M6,19H10V5H6V19Z");
    private const string SkipNextIconPath = "M16,18H18V6H16M6,18L14.5,12L6,6V18Z";
    private const string SkipPreviousIconPath = "M6,6H8V18H6V6M9.5,12L18,6V18L9.5,12Z";
    private const string ShuffleIconPath = "M14.83,13.41L13.42,14.82L16.55,17.95L14.83,19.66H19.83V14.66L18.24,16.25L15.11,13.12M14.83,10.59L16.55,8.87L15.11,7.43L18.24,4.3L19.83,5.89V0.89H14.83L16.55,2.61L14.83,4.32L16.24,5.73M4,4H8.5L18,17H20V19H18.5L15,14.5L9,19H4V17H8L14,4.5L8.5,4H4V4Z";
    private const string RepeatIconPath = "M17,17H7V14L3,18L7,22V19H19V13H17M7,7H17V10L21,6L17,2V5H5V11H7V7Z";
    private const string PowerIconPath = "M16.56,5.44L15.11,6.89C16.84,7.94 18,9.83 18,12A6,6 0 0,1 12,18A6,6 0 0,1 6,12C6,9.83 7.16,7.94 8.88,6.88L7.44,5.44C5.36,6.88 4,9.28 4,12A8,8 0 0,0 12,20A8,8 0 0,0 20,12C20,9.28 18.64,6.88 16.56,5.44M13,3H11V13H13";
    private const string VolumeIconPath = "M14,3.23V5.29C16.89,6.15 19,8.83 19,12C19,15.17 16.89,17.85 14,18.71V20.77C18,19.86 21,16.28 21,12C21,7.72 18,4.14 14,3.23M16.5,12C16.5,10.23 15.5,8.71 14,7.97V16C15.5,15.29 16.5,13.76 16.5,12M3,9V15H7L12,20V4L7,9H3Z";

    // The art is shown twice — a 40px thumbnail and a heavily blurred card background — so neither
    // needs anything like a cover image's native resolution.
    private const int AlbumArtPixelWidth = 320;

    private string? _entityId;
    private string? _lastArtUrl;
    private Bitmap? _albumArt;
    private bool _suppressVolumeEvents;
    private bool _useAlbumArtBackground = true;

    private bool _suppressProgressEvents;
    private bool _seekSupported;
    private double? _durationSeconds;
    private double _positionAtUpdateSeconds;
    private DateTimeOffset _positionUpdatedAtUtc;
    private bool _isPlaying;
    private bool _isOnScreen;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MediaPlayerWidget()
    {
        InitializeComponent();
        PreviousIcon.Data = Geometry.Parse(SkipPreviousIconPath);
        NextIcon.Data = Geometry.Parse(SkipNextIconPath);
        ShuffleIcon.Data = Geometry.Parse(ShuffleIconPath);
        RepeatIcon.Data = Geometry.Parse(RepeatIconPath);
        PowerIcon.Data = Geometry.Parse(PowerIconPath);
        VolumeButtonIcon.Data = Geometry.Parse(VolumeIconPath);

        PreviousButton.Click += (_, _) => CallService("media_previous_track");
        PlayPauseButton.Click += (_, _) => CallService("media_play_pause");
        NextButton.Click += (_, _) => CallService("media_next_track");
        ShuffleButton.Click += (_, _) => ToggleShuffle();
        RepeatButton.Click += (_, _) => CycleRepeat();
        PowerButton.Click += (_, _) => TogglePower();
        VolumeSlider.PropertyChanged += OnVolumeSliderChanged;
        ProgressSlider.PropertyChanged += OnProgressSliderChanged;
        _progressTimer.Tick += (_, _) => RenderProgress();
        WindowVisibility.Track(this, isOnScreen =>
        {
            _isOnScreen = isOnScreen;
            UpdateProgressTimer();
        });
    }

    public void SetContent(HaEntityState state, bool useAlbumArtBackground = true)
    {
        _entityId = state.EntityId;
        _useAlbumArtBackground = useAlbumArtBackground;

        var features = state.Attributes.TryGetValue("supported_features", out var sf) && sf is not null
            ? (Feature)Convert.ToInt64(sf)
            : (Feature)0;

        var isOff = state.State is "off" or "unavailable" or "unknown";

        var title = state.Attributes.TryGetValue("media_title", out var t) && t is string ts && !string.IsNullOrEmpty(ts)
            ? ts
            : isOff ? Loc.Instance.Tr("Media.Off") : HaEntityDisplay.LabelFor(state);
        var artist = state.Attributes.TryGetValue("media_artist", out var a) && a is string artistText && !string.IsNullOrEmpty(artistText)
            ? artistText
            : state.Attributes.TryGetValue("app_name", out var app) ? app?.ToString() : null;
        var source = state.Attributes.TryGetValue("source", out var src) ? src?.ToString() : null;
        var subtitle = artist is not null && source is not null ? $"{artist} · {source}" : artist ?? source;

        MediaTitleText.Text = title;
        MediaArtistText.Text = subtitle ?? "";

        var isPlaying = state.State == "playing";
        PlayPauseIcon.Data = isPlaying ? PauseIcon : PlayIcon;
        PlayPauseButton.IsEnabled = !isOff;
        PreviousButton.IsEnabled = !isOff && features.HasFlag(Feature.PreviousTrack);
        NextButton.IsEnabled = !isOff && features.HasFlag(Feature.NextTrack);

        PowerButton.IsVisible = features.HasFlag(Feature.TurnOn) || features.HasFlag(Feature.TurnOff);
        PowerButton.Opacity = isOff ? 0.5 : 1.0;

        ShuffleButton.IsVisible = features.HasFlag(Feature.Shuffle);
        var shuffleOn = state.Attributes.TryGetValue("shuffle", out var sh) && sh is bool shb && shb;
        ShuffleButton.Opacity = shuffleOn ? 1.0 : 0.5;

        RepeatButton.IsVisible = features.HasFlag(Feature.Repeat);
        var repeatMode = state.Attributes.TryGetValue("repeat", out var rp) ? rp?.ToString() : "off";
        RepeatButton.Opacity = repeatMode is "all" or "one" ? 1.0 : 0.5;

        var volumeVisible = features.HasFlag(Feature.VolumeSet) && !isOff;
        VolumeButton.IsVisible = volumeVisible;
        if (volumeVisible)
        {
            var volume = state.Attributes.TryGetValue("volume_level", out var vol) && vol is not null
                ? Convert.ToDouble(vol)
                : 0.0;
            _suppressVolumeEvents = true;
            VolumeSlider.Value = volume;
            _suppressVolumeEvents = false;
        }

        _seekSupported = features.HasFlag(Feature.Seek);
        _isPlaying = isPlaying;
        var duration = state.Attributes.TryGetValue("media_duration", out var dur) && dur is not null ? Convert.ToDouble(dur) : (double?)null;
        if (!isOff && duration is > 0)
        {
            _durationSeconds = duration;
            _positionAtUpdateSeconds = state.Attributes.TryGetValue("media_position", out var pos) && pos is not null ? Convert.ToDouble(pos) : 0;
            _positionUpdatedAtUtc = state.Attributes.TryGetValue("media_position_updated_at", out var updatedAt) && updatedAt is not null
                && DateTimeOffset.TryParse(updatedAt.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            ProgressRow.IsVisible = true;
            ProgressSlider.IsHitTestVisible = _seekSupported;
            RenderProgress();
        }
        else
        {
            _durationSeconds = null;
            ProgressRow.IsVisible = false;
        }
        UpdateProgressTimer();

        var artUrl = state.Attributes.TryGetValue("entity_picture", out var pic) ? pic?.ToString() : null;
        if (!string.IsNullOrEmpty(artUrl) && !isOff)
        {
            if (artUrl != _lastArtUrl)
            {
                _lastArtUrl = artUrl;
                _ = LoadAlbumArtAsync(artUrl);
            }
            else
            {
                // Same art as last time — LoadAlbumArtAsync won't re-fire, so just make sure
                // the background reflects the (possibly just-toggled) setting immediately.
                SetBackgroundVisible(_useAlbumArtBackground && _albumArt is not null);
            }
        }
        else
        {
            _lastArtUrl = null;
            SetAlbumArt(null);
            SetBackgroundVisible(false);
        }
    }

    /// <summary>The position readout only needs to tick while something is playing and the flyout is actually open; it catches up from the last known position the moment it's shown again.</summary>
    private void UpdateProgressTimer()
    {
        if (_isOnScreen && _isPlaying && _durationSeconds is not null)
        {
            RenderProgress();
            _progressTimer.Start();
        }
        else
        {
            _progressTimer.Stop();
        }
    }

    private void SetBackgroundVisible(bool visible)
    {
        BackgroundArtImage.IsVisible = visible;
        BackgroundTint.IsVisible = visible;
        NoiseOverlay.IsVisible = visible;
    }

    /// <summary>Swaps the bitmap behind both the thumbnail and the card background, freeing the previous one immediately rather than whenever its finalizer happens to run.</summary>
    private void SetAlbumArt(Bitmap? bitmap)
    {
        var previous = _albumArt;
        _albumArt = bitmap;
        AlbumArtImage.Source = bitmap;
        BackgroundArtImage.Source = bitmap;
        previous?.Dispose();
    }

    private async Task LoadAlbumArtAsync(string rawUrl)
    {
        if (HaSession.Client is not { } client) return;

        var bytes = await client.DownloadImageAsync(rawUrl);
        if (bytes is null) return; // best effort — leave the art as it was rather than show a broken image

        Bitmap bitmap;
        try
        {
            bitmap = await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(stream, AlbumArtPixelWidth);
            });
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            return; // not a decodable image
        }

        if (_lastArtUrl != rawUrl) // superseded by a newer track while we were downloading
        {
            bitmap.Dispose();
            return;
        }

        SetAlbumArt(bitmap);
        SetBackgroundVisible(_useAlbumArtBackground);
    }

    private void RenderProgress()
    {
        if (_durationSeconds is not { } duration || duration <= 0) return;

        var elapsed = _isPlaying ? (DateTimeOffset.UtcNow - _positionUpdatedAtUtc).TotalSeconds : 0;
        var position = Math.Clamp(_positionAtUpdateSeconds + elapsed, 0, duration);

        _suppressProgressEvents = true;
        ProgressSlider.Maximum = duration;
        ProgressSlider.Value = position;
        _suppressProgressEvents = false;

        PositionText.Text = FormatTime(position);
        DurationText.Text = FormatTime(duration);
    }

    private static string FormatTime(double totalSeconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return span.Hours > 0 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    private void OnProgressSliderChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (_suppressProgressEvents || !_seekSupported || e.Property != RangeBase.ValueProperty) return;
        var value = (double)e.NewValue!;

        // A user drag is the only source of ValueChanged once _seekSupported is true and
        // RenderProgress's own updates are guarded by _suppressProgressEvents — safe to seek.
        _positionAtUpdateSeconds = value;
        _positionUpdatedAtUtc = DateTimeOffset.UtcNow;
        CallService("media_seek", new JsonObject { ["seek_position"] = value });
    }

    private void OnVolumeSliderChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (_suppressVolumeEvents || e.Property != RangeBase.ValueProperty) return;
        CallService("volume_set", new JsonObject { ["volume_level"] = (double)e.NewValue! });
    }

    private void ToggleShuffle()
    {
        var currentlyOn = ShuffleButton.Opacity > 0.9;
        CallService("shuffle_set", new JsonObject { ["shuffle"] = !currentlyOn });
    }

    private void CycleRepeat()
    {
        var currentlyOn = RepeatButton.Opacity > 0.9;
        CallService("repeat_set", new JsonObject { ["repeat"] = currentlyOn ? "off" : "all" });
    }

    private void TogglePower()
    {
        var isOff = PowerButton.Opacity < 0.9;
        CallService(isOff ? "turn_on" : "turn_off");
    }

    private void CallService(string service, JsonObject? data = null)
    {
        if (_entityId is not null) _ = HaActions.CallAsync("media_player", service, _entityId, data);
    }
}
