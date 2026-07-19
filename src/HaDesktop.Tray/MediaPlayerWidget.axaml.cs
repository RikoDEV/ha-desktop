using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
    private const string PlayIconPath = "M8,5.14V19.14L19,12.14L8,5.14Z";
    private const string PauseIconPath = "M14,19H18V5H14M6,19H10V5H6V19Z";
    private const string SkipNextIconPath = "M16,18H18V6H16M6,18L14.5,12L6,6V18Z";
    private const string SkipPreviousIconPath = "M6,6H8V18H6V6M9.5,12L18,6V18L9.5,12Z";
    private const string ShuffleIconPath = "M14.83,13.41L13.42,14.82L16.55,17.95L14.83,19.66H19.83V14.66L18.24,16.25L15.11,13.12M14.83,10.59L16.55,8.87L15.11,7.43L18.24,4.3L19.83,5.89V0.89H14.83L16.55,2.61L14.83,4.32L16.24,5.73M4,4H8.5L18,17H20V19H18.5L15,14.5L9,19H4V17H8L14,4.5L8.5,4H4V4Z";
    private const string RepeatIconPath = "M17,17H7V14L3,18L7,22V19H19V13H17M7,7H17V10L21,6L17,2V5H5V11H7V7Z";
    private const string PowerIconPath = "M16.56,5.44L15.11,6.89C16.84,7.94 18,9.83 18,12A6,6 0 0,1 12,18A6,6 0 0,1 6,12C6,9.83 7.16,7.94 8.88,6.88L7.44,5.44C5.36,6.88 4,9.28 4,12A8,8 0 0,0 12,20A8,8 0 0,0 20,12C20,9.28 18.64,6.88 16.56,5.44M13,3H11V13H13";
    private const string VolumeIconPath = "M14,3.23V5.29C16.89,6.15 19,8.83 19,12C19,15.17 16.89,17.85 14,18.71V20.77C18,19.86 21,16.28 21,12C21,7.72 18,4.14 14,3.23M16.5,12C16.5,10.23 15.5,8.71 14,7.97V16C15.5,15.29 16.5,13.76 16.5,12M3,9V15H7L12,20V4L7,9H3Z";

    private static readonly HttpClient ImageHttp = new();

    private HaClient? _client;
    private string? _entityId;
    private string? _lastArtUrl;
    private bool _suppressVolumeEvents;
    private bool _useAlbumArtBackground = true;

    private bool _suppressProgressEvents;
    private bool _seekSupported;
    private double? _durationSeconds;
    private double _positionAtUpdateSeconds;
    private DateTimeOffset _positionUpdatedAtUtc;
    private bool _isPlaying;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MediaPlayerWidget()
    {
        InitializeComponent();
        this.FindControl<PathIcon>("PreviousIcon")!.Data = Geometry.Parse(SkipPreviousIconPath);
        this.FindControl<PathIcon>("NextIcon")!.Data = Geometry.Parse(SkipNextIconPath);
        this.FindControl<PathIcon>("ShuffleIcon")!.Data = Geometry.Parse(ShuffleIconPath);
        this.FindControl<PathIcon>("RepeatIcon")!.Data = Geometry.Parse(RepeatIconPath);
        this.FindControl<PathIcon>("PowerIcon")!.Data = Geometry.Parse(PowerIconPath);
        this.FindControl<PathIcon>("VolumeIcon")!.Data = Geometry.Parse(VolumeIconPath);

        this.FindControl<Button>("PreviousButton")!.Click += (_, _) => CallService("media_previous_track");
        this.FindControl<Button>("PlayPauseButton")!.Click += (_, _) => CallService("media_play_pause");
        this.FindControl<Button>("NextButton")!.Click += (_, _) => CallService("media_next_track");
        this.FindControl<Button>("ShuffleButton")!.Click += (_, _) => ToggleShuffle();
        this.FindControl<Button>("RepeatButton")!.Click += (_, _) => CycleRepeat();
        this.FindControl<Button>("PowerButton")!.Click += (_, _) => TogglePower();
        this.FindControl<Slider>("VolumeSlider")!.PropertyChanged += OnVolumeSliderChanged;
        this.FindControl<Slider>("ProgressSlider")!.PropertyChanged += OnProgressSliderChanged;
        _progressTimer.Tick += (_, _) => RenderProgress();
        DetachedFromVisualTree += (_, _) => _progressTimer.Stop();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void SetContent(HaEntityState state, HaClient client, HaConnectionSettings connectionSettings, bool useAlbumArtBackground = true)
    {
        _client = client;
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

        this.FindControl<TextBlock>("MediaTitleText")!.Text = title;
        this.FindControl<TextBlock>("MediaArtistText")!.Text = subtitle ?? "";

        var isPlaying = state.State == "playing";
        this.FindControl<PathIcon>("PlayPauseIcon")!.Data = Geometry.Parse(isPlaying ? PauseIconPath : PlayIconPath);
        this.FindControl<Button>("PlayPauseButton")!.IsEnabled = !isOff;
        this.FindControl<Button>("PreviousButton")!.IsEnabled = !isOff && features.HasFlag(Feature.PreviousTrack);
        this.FindControl<Button>("NextButton")!.IsEnabled = !isOff && features.HasFlag(Feature.NextTrack);

        var powerButton = this.FindControl<Button>("PowerButton")!;
        powerButton.IsVisible = features.HasFlag(Feature.TurnOn) || features.HasFlag(Feature.TurnOff);
        powerButton.Opacity = isOff ? 0.5 : 1.0;

        var shuffleButton = this.FindControl<Button>("ShuffleButton")!;
        shuffleButton.IsVisible = features.HasFlag(Feature.Shuffle);
        var shuffleOn = state.Attributes.TryGetValue("shuffle", out var sh) && sh is bool shb && shb;
        shuffleButton.Opacity = shuffleOn ? 1.0 : 0.5;

        var repeatButton = this.FindControl<Button>("RepeatButton")!;
        repeatButton.IsVisible = features.HasFlag(Feature.Repeat);
        var repeatMode = state.Attributes.TryGetValue("repeat", out var rp) ? rp?.ToString() : "off";
        repeatButton.Opacity = repeatMode is "all" or "one" ? 1.0 : 0.5;

        var volumeVisible = features.HasFlag(Feature.VolumeSet) && !isOff;
        this.FindControl<PathIcon>("VolumeIcon")!.IsVisible = volumeVisible;
        this.FindControl<Slider>("VolumeSlider")!.IsVisible = volumeVisible;
        if (volumeVisible)
        {
            var volume = state.Attributes.TryGetValue("volume_level", out var vol) && vol is not null
                ? Convert.ToDouble(vol)
                : 0.0;
            _suppressVolumeEvents = true;
            this.FindControl<Slider>("VolumeSlider")!.Value = volume;
            _suppressVolumeEvents = false;
        }

        _seekSupported = features.HasFlag(Feature.Seek);
        _isPlaying = isPlaying;
        var progressRow = this.FindControl<Grid>("ProgressRow")!;
        var duration = state.Attributes.TryGetValue("media_duration", out var dur) && dur is not null ? Convert.ToDouble(dur) : (double?)null;
        if (!isOff && duration is > 0)
        {
            _durationSeconds = duration;
            _positionAtUpdateSeconds = state.Attributes.TryGetValue("media_position", out var pos) && pos is not null ? Convert.ToDouble(pos) : 0;
            _positionUpdatedAtUtc = state.Attributes.TryGetValue("media_position_updated_at", out var updatedAt) && updatedAt is not null
                && DateTimeOffset.TryParse(updatedAt.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            progressRow.IsVisible = true;
            this.FindControl<Slider>("ProgressSlider")!.IsHitTestVisible = _seekSupported;
            RenderProgress();

            if (isPlaying) _progressTimer.Start();
            else _progressTimer.Stop();
        }
        else
        {
            _durationSeconds = null;
            _progressTimer.Stop();
            progressRow.IsVisible = false;
        }

        var artUrl = state.Attributes.TryGetValue("entity_picture", out var pic) ? pic?.ToString() : null;
        if (!string.IsNullOrEmpty(artUrl) && !isOff)
        {
            if (artUrl != _lastArtUrl)
            {
                _lastArtUrl = artUrl;
                _ = LoadAlbumArtAsync(artUrl, connectionSettings);
            }
            else
            {
                // Same art as last time — LoadAlbumArtAsync won't re-fire, so just make sure
                // the background reflects the (possibly just-toggled) setting immediately.
                SetBackgroundVisible(_useAlbumArtBackground);
            }
        }
        else
        {
            _lastArtUrl = null;
            this.FindControl<Image>("AlbumArtImage")!.Source = null;
            SetBackgroundVisible(false);
        }
    }

    private void SetBackgroundVisible(bool visible)
    {
        this.FindControl<Image>("BackgroundArtImage")!.IsVisible = visible;
        this.FindControl<Border>("BackgroundTint")!.IsVisible = visible;
    }

    private async Task LoadAlbumArtAsync(string rawUrl, HaConnectionSettings settings)
    {
        try
        {
            Uri uri;
            var needsAuth = false;
            if (rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                uri = new Uri(rawUrl);
            }
            else
            {
                uri = new Uri(new Uri(settings.BaseUrl.TrimEnd('/') + "/"), rawUrl.TrimStart('/'));
                needsAuth = true;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (needsAuth)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);

            using var response = await ImageHttp.SendAsync(request);
            if (!response.IsSuccessStatusCode) return;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            memory.Position = 0;

            var bitmap = new Bitmap(memory);
            if (_lastArtUrl == rawUrl) // still the current track — not superseded while we were downloading
            {
                this.FindControl<Image>("AlbumArtImage")!.Source = bitmap;
                this.FindControl<Image>("BackgroundArtImage")!.Source = bitmap;
                SetBackgroundVisible(_useAlbumArtBackground);
            }
        }
        catch
        {
            // best effort — leave the art blank rather than show a broken image
        }
    }

    private void RenderProgress()
    {
        if (_durationSeconds is not { } duration || duration <= 0) return;

        var elapsed = _isPlaying ? (DateTimeOffset.UtcNow - _positionUpdatedAtUtc).TotalSeconds : 0;
        var position = Math.Clamp(_positionAtUpdateSeconds + elapsed, 0, duration);

        _suppressProgressEvents = true;
        var slider = this.FindControl<Slider>("ProgressSlider")!;
        slider.Maximum = duration;
        slider.Value = position;
        _suppressProgressEvents = false;

        this.FindControl<TextBlock>("PositionText")!.Text = FormatTime(position);
        this.FindControl<TextBlock>("DurationText")!.Text = FormatTime(duration);
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
        if (_client is null || _entityId is null) return;

        // A user drag is the only source of ValueChanged once _seekSupported is true and
        // RenderProgress's own updates are guarded by _suppressProgressEvents — safe to seek.
        _positionAtUpdateSeconds = value;
        _positionUpdatedAtUtc = DateTimeOffset.UtcNow;
        _ = TrySeekAsync(value);
    }

    private async Task TrySeekAsync(double seconds)
    {
        try { await _client!.CallServiceAsync("media_player", "media_seek", _entityId!, new JsonObject { ["seek_position"] = seconds }); }
        catch { /* best effort */ }
    }

    private void OnVolumeSliderChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (_suppressVolumeEvents || e.Property != RangeBase.ValueProperty) return;
        var value = (double)e.NewValue!;
        if (_client is null || _entityId is null) return;
        _ = TrySetVolumeAsync(value);
    }

    private async Task TrySetVolumeAsync(double value)
    {
        try { await _client!.CallServiceAsync("media_player", "volume_set", _entityId!, new JsonObject { ["volume_level"] = value }); }
        catch { /* best effort */ }
    }

    private void ToggleShuffle()
    {
        if (_client is null || _entityId is null) return;
        var currentlyOn = this.FindControl<Button>("ShuffleButton")!.Opacity > 0.9;
        _ = TryCallServiceAsync("shuffle_set", new JsonObject { ["shuffle"] = !currentlyOn });
    }

    private void CycleRepeat()
    {
        if (_client is null || _entityId is null) return;
        var current = this.FindControl<Button>("RepeatButton")!.Opacity > 0.9 ? "all" : "off";
        var next = current == "off" ? "all" : "off";
        _ = TryCallServiceAsync("repeat_set", new JsonObject { ["repeat"] = next });
    }

    private void TogglePower()
    {
        if (_client is null || _entityId is null) return;
        var isOff = this.FindControl<Button>("PowerButton")!.Opacity < 0.9;
        CallService(isOff ? "turn_on" : "turn_off");
    }

    private void CallService(string service)
    {
        if (_client is null || _entityId is null) return;
        _ = TryCallServiceAsync(service);
    }

    private async Task TryCallServiceAsync(string service, JsonObject? data = null)
    {
        try { await _client!.CallServiceAsync("media_player", service, _entityId!, data); }
        catch { /* best effort */ }
    }
}
