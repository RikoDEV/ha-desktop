using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HaDesktop.Core.Autostart;
using HaDesktop.Core.Diagnostics;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Sensors;
using HaDesktop.Core.Storage;
using HaDesktop.Core.Updates;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

public partial class SettingsWindow : Window
{
    private static readonly string[] PageNames = { "ConnectionPage", "TilesPage", "AppearancePage", "SensorsPage", "SystemPage", "NotificationsPage", "AboutPage" };

    // Keyed by nav icon control name, in NavList/PageNames order.
    private static readonly (string IconName, string IconKey)[] NavIcons =
    {
        ("ConnectionNavIcon", "wifi"),
        ("TilesNavIcon", "grid"),
        ("AppearanceNavIcon", "palette"),
        ("SensorsNavIcon", "motion-sensor"),
        ("SystemNavIcon", "cog"),
        ("NotificationsNavIcon", "bell"),
        ("AboutNavIcon", "info"),
    };

    public SettingsWindow()
    {
        InitializeComponent();

        foreach (var (iconName, iconKey) in NavIcons)
            this.FindControl<PathIcon>(iconName)!.Data = TileIcons.GeometryFor(iconKey);

        TileEditorHost.Content = new TileLayoutEditor();

        UpdateConnectionUi();
        LoadSensorUi();
        LoadAppearanceUi();
        LoadLanguageUi();
        LoadAboutUi();
        LoadNotificationsUi();
        _ = LoadAutostartStateAsync();
        _ = LoadWeatherUiAsync();
        _ = LoadMediaPlayerUiAsync();
        _ = TestGpuAvailabilityAsync();

        // Set after InitializeComponent, not via XAML SelectedIndex="0" — that fires
        // SelectionChanged during EndInit, before the window's name scope is fully
        // populated, so FindControl calls inside the handler throw.
        NavList.SelectedIndex = 0;

        AppSettings.ConnectionChanged += OnConnectionChanged;
        Loc.Instance.LanguageChanged += OnLanguageChangedRefresh;
        Closed += (_, _) =>
        {
            AppSettings.ConnectionChanged -= OnConnectionChanged;
            Loc.Instance.LanguageChanged -= OnLanguageChangedRefresh;
        };
        _ = RefreshUpdatesThenInstanceInfoAsync();
        _ = CheckForAppUpdateAsync();
    }

    /// <summary>
    /// Runs sequentially, not as two independent fire-and-forget calls — isolates the two so a
    /// problem in the newer instance-info fetch (system_health/info, supervisor/api) can't also
    /// take down the Updates card by sharing a hung connection.
    /// </summary>
    private async Task RefreshUpdatesThenInstanceInfoAsync()
    {
        await RefreshUpdatesAsync();
        await RefreshInstanceInfoAsync();
    }

    private void LoadAboutUi()
    {
        AboutAppIcon.Data = TileIcons.GeometryFor("cover");

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        AboutVersionText.Text = version is null ? Loc.Instance.Tr("About.DevelopmentBuild") : version.ToString(3);

        UpdateCheckToggle.IsChecked = AppSettings.UpdateCheckEnabled;
        RenderUpdateStatus();
    }

    private void OnAuthorLinkClicked(object? sender, RoutedEventArgs e)
    {
        ShellLauncher.TryOpen("https://riko.dev");
    }

    private void OnGitHubLinkClicked(object? sender, RoutedEventArgs e)
    {
        ShellLauncher.TryOpen("https://github.com/RikoDEV/ha-desktop");
    }

    private void OnViewReleaseClicked(object? sender, RoutedEventArgs e)
    {
        if (_latestReleaseUrl is null) return;
        ShellLauncher.TryOpen(_latestReleaseUrl);
    }

    private async void OnUpdateCheckToggled(object? sender, RoutedEventArgs e)
    {
        var isChecked = UpdateCheckToggle.IsChecked == true;
        await AppSettings.SetUpdateCheckEnabledAsync(isChecked);

        if (isChecked)
        {
            _ = CheckForAppUpdateAsync();
        }
        else
        {
            _appUpdateStatus = null;
            _latestVersionLabel = null;
            _latestReleaseUrl = null;
            RenderUpdateStatus();
        }
    }

    private async void OnCheckForUpdatesClicked(object? sender, RoutedEventArgs e) => await CheckForAppUpdateAsync();

    private AppUpdateCheckStatus? _appUpdateStatus;
    private string? _latestVersionLabel;
    private string? _latestReleaseUrl;
    private int _appUpdateCheckToken;

    /// <summary>
    /// Runs on Settings open and on "Check Now" — respects the toggle (a manual click still checks
    /// even while auto-check is off, same as HA's own Updates card lets you refresh regardless of
    /// any polling setting). Never surfaces network errors as a false "you're up to date"; see
    /// <see cref="GitHubUpdateChecker"/>.
    /// </summary>
    private async Task CheckForAppUpdateAsync()
    {
        if (!AppSettings.UpdateCheckEnabled)
        {
            RenderUpdateStatus();
            return;
        }

        var myToken = ++_appUpdateCheckToken;
        _appUpdateStatus = null;
        RenderUpdateStatus();
        CheckForUpdatesButton.IsEnabled = false;

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var result = version is null
            ? AppUpdateCheckResult.Failed
            : await GitHubUpdateChecker.CheckAsync(version);

        if (myToken != _appUpdateCheckToken) return; // superseded by a later check while we were awaiting

        CheckForUpdatesButton.IsEnabled = true;
        _appUpdateStatus = result.Status;
        _latestVersionLabel = result.LatestVersion;
        _latestReleaseUrl = result.ReleaseUrl;
        RenderUpdateStatus();
    }

    /// <summary>Re-renders from the last known result — called after a real check and again on language
    /// switch, so switching languages doesn't re-hit GitHub's API just to relocalize the same status.</summary>
    private void RenderUpdateStatus()
    {
        var statusText = UpdateStatusText;
        var viewReleaseButton = ViewReleaseButton;

        if (!AppSettings.UpdateCheckEnabled)
        {
            statusText.Text = Loc.Instance.Tr("Update.Disabled");
            viewReleaseButton.IsVisible = false;
            return;
        }

        statusText.Text = _appUpdateStatus switch
        {
            null => Loc.Instance.Tr("Update.Checking"),
            AppUpdateCheckStatus.UpdateAvailable => Loc.Instance.Tr("Update.Available", _latestVersionLabel),
            AppUpdateCheckStatus.Failed => Loc.Instance.Tr("Update.Failed"),
            _ => Loc.Instance.Tr("Update.UpToDate"),
        };
        viewReleaseButton.IsVisible = _appUpdateStatus == AppUpdateCheckStatus.UpdateAvailable;
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selectedIndex = NavList.SelectedIndex;
        for (var i = 0; i < PageNames.Length; i++)
            this.FindControl<StackPanel>(PageNames[i])!.IsVisible = i == selectedIndex;
    }

    private void OnConnectionChanged() => Dispatcher.UIThread.Post(() =>
    {
        UpdateConnectionUi();
        _ = RefreshUpdatesThenInstanceInfoAsync();
    });

    /// <summary>
    /// Refreshes the dynamic (code-behind-set) text that a language switch doesn't otherwise touch —
    /// XAML-bound static labels refresh on their own via the {loc:Tr} indexer binding, but text built
    /// with string interpolation (connection status, device slug preview, tile tooltips, etc.) needs
    /// to be regenerated in the new language explicitly.
    /// </summary>
    private void OnLanguageChangedRefresh() => Dispatcher.UIThread.Post(() =>
    {
        UpdateConnectionUi();
        LoadAboutUi();
        LoadNotificationsUi();
        UpdateDeviceSlugPreview(DeviceNameBox.Text?.Trim() is { Length: > 0 } name ? name : AppSettings.SensorPrefs.DeviceName);
        _ = RefreshUpdatesThenInstanceInfoAsync();
    });

    private int _updatesRefreshToken;

    private static readonly HashSet<string> UpdateAttributes = new() { "friendly_name", "installed_version", "latest_version" };
    private static readonly HashSet<string> NameAttribute = new() { "friendly_name" };

    /// <summary>The entities of one domain, with just their names — for the weather/media player entity pickers.</summary>
    private static Task<List<HaEntityState>> GetEntitiesOfDomainAsync(HaClient client, string domain) =>
        client.GetStatesAsync(id => id.StartsWith(domain + ".", StringComparison.Ordinal), NameAttribute);

    private async Task RefreshUpdatesAsync()
    {
        var myToken = ++_updatesRefreshToken;
        var card = UpdatesCard;
        var panel = UpdatesPanel;

        if (HaSession.Client is not { } client)
        {
            card.IsVisible = false;
            return;
        }

        List<HaEntityState> states;
        try
        {
            states = await client.GetStatesAsync(id => id.StartsWith("update.", StringComparison.Ordinal), UpdateAttributes);
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            if (myToken == _updatesRefreshToken) card.IsVisible = false;
            return;
        }

        if (myToken != _updatesRefreshToken) return; // superseded by a later call while we were awaiting

        // Match HA's own Updates view: only entities with a pending update (state "on"),
        // not the full list of update.* entities (most of which are just up to date).
        var pending = states.Where(s => s.State == "on")
            .OrderBy(HaEntityDisplay.LabelFor, StringComparer.OrdinalIgnoreCase)
            .ToList();

        panel.Children.Clear();
        card.IsVisible = true;

        if (pending.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = Loc.Instance.Tr("Updates.None"), FontSize = 12, Opacity = 0.7 });
            return;
        }

        foreach (var update in pending)
        {
            var installed = update.Attributes.TryGetValue("installed_version", out var iv) ? iv?.ToString() : null;
            var latest = update.Attributes.TryGetValue("latest_version", out var lv) ? lv?.ToString() : null;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var textPanel = new StackPanel { Spacing = 2, [Grid.ColumnProperty] = 0 };
            textPanel.Children.Add(new TextBlock { Text = HaEntityDisplay.LabelFor(update), FontSize = 13, FontWeight = FontWeight.SemiBold });
            textPanel.Children.Add(new TextBlock { Text = $"{installed ?? "?"} → {latest ?? "?"}", FontSize = 12, Foreground = Brushes.DarkOrange });
            row.Children.Add(textPanel);

            var updateButton = new Button { Content = Loc.Instance.Tr("Updates.Update"), VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 1 };
            var entityId = update.EntityId;
            updateButton.Click += async (_, _) =>
            {
                updateButton.IsEnabled = false;
                updateButton.Content = Loc.Instance.Tr("Updates.Updating");
                try
                {
                    await HaSession.Client!.CallServiceAsync("update", "install", entityId);
                }
                catch (Exception ex)
                {
                    Log.Swallowed(ex);
                    // best effort — button re-enables below regardless so the user can retry
                }
                await RefreshUpdatesAsync();
            };
            row.Children.Add(updateButton);

            panel.Children.Add(row);
        }
    }

    private int _instanceInfoRefreshToken;

    private async Task RefreshInstanceInfoAsync()
    {
        var myToken = ++_instanceInfoRefreshToken;
        var card = InstanceInfoCard;
        var panel = InstanceInfoPanel;

        if (HaSession.Client is not { } client)
        {
            card.IsVisible = false;
            return;
        }

        // Core version needs no round trip at all (HaVersion is cached from the connection
        // handshake) — show the card immediately with just that row instead of waiting on
        // system_health/info and the Supervisor-only calls, which can be slow, or fail outright
        // on a non-admin account or a Core/Container install with no Supervisor. Previously the
        // whole card stayed hidden until every one of those calls resolved, so a single slow or
        // failing piece hid a Core version that was available instantly.
        panel.Children.Clear();
        card.IsVisible = true;
        AddInstanceInfoRow(panel, Loc.Instance.Tr("Instance.Core"), client.HaVersion);

        HaInstanceInfo info;
        try
        {
            // system_health/info runs a health check across every loaded integration, some of
            // which make their own network calls (connectivity checks, cloud status, etc.) — a
            // real instance can easily take several seconds to answer, not just a hang. Since
            // Core is already showing and this no longer risks blocking Updates (sequential,
            // not concurrent — see RefreshUpdatesThenInstanceInfoAsync), there's little cost to
            // giving it a generous cap rather than cutting it off too early.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            info = await client.GetInstanceInfoAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            return; // Core row (already shown) stands on its own; the rest just isn't available
        }

        if (myToken != _instanceInfoRefreshToken) return; // superseded by a later call while we were awaiting

        // Inserted above the already-shown Core row so the final order reads Installation
        // Method, Core, Supervisor, Operating System — matching HA's own About page — even
        // though Core was added first (synchronously, before this async fetch resolved).
        AddInstanceInfoRow(panel, Loc.Instance.Tr("Instance.InstallationMethod"), info.InstallationType, index: 0);
        AddInstanceInfoRow(panel, Loc.Instance.Tr("Instance.Supervisor"), info.SupervisorVersion);
        AddInstanceInfoRow(panel, Loc.Instance.Tr("Instance.OperatingSystem"), info.OsVersion);
    }

    private static void AddInstanceInfoRow(StackPanel panel, string label, string? value, int? index = null)
    {
        if (value is null) return; // e.g. Supervisor/OS don't exist on Core or Container installs

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock { Text = label, FontSize = 13, Opacity = 0.7, [Grid.ColumnProperty] = 0 });
        row.Children.Add(new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeight.SemiBold, [Grid.ColumnProperty] = 1 });

        if (index is { } i) panel.Children.Insert(i, row);
        else panel.Children.Add(row);
    }

    /// <summary>Shows either the "connected to X" summary or the sign-in form, depending on live connection state.</summary>
    private void UpdateConnectionUi()
    {
        var isConnected = HaSession.Client is not null && HaSession.Credentials is not null;

        ConnectedPanel.IsVisible = isConnected;
        SignInPanel.IsVisible = !isConnected;
        CancelSwitchButton.IsVisible = false;

        if (isConnected)
        {
            ConnectedUrlText.Text = Loc.Instance.Tr("Connection.ConnectedTo", HaSession.Credentials!.BaseUrl);
        }
        else
        {
            BaseUrlBox.Text = string.Empty;
            StatusText.Text = string.Empty;
            LoginButton.IsEnabled = true;
        }
    }

    private async Task LoadAutostartStateAsync()
    {
        var isEnabled = await AutostartManager.Current.IsEnabledAsync();
        AutostartCheckBox.IsChecked = isEnabled;
    }

    // Matches BaseUrlBox's watermark — if the user hits sign-in without typing a URL,
    // assume the common local mDNS address rather than blocking on an empty field.
    private const string DefaultBaseUrl = "http://homeassistant.local:8123";

    private async void OnLoginClicked(object? sender, RoutedEventArgs e)
    {
        var status = StatusText;
        var button = LoginButton;
        var baseUrl = BaseUrlBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = DefaultBaseUrl;

        button.IsEnabled = false;
        status.Text = Loc.Instance.Tr("Connection.OpeningBrowser");
        status.Foreground = Brushes.Gray;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var credentials = await HaOAuthLogin.LoginAsync(baseUrl, cts.Token);

            status.Text = Loc.Instance.Tr("Connection.Connecting");
            await HaSession.ConnectWithOAuthAsync(credentials);
            // UpdateConnectionUi() runs via the ConnectionChanged event this raises.
        }
        catch (OperationCanceledException)
        {
            status.Text = Loc.Instance.Tr("Connection.SignInTimedOut");
            status.Foreground = Brushes.OrangeRed;
            button.IsEnabled = true;
        }
        catch (Exception ex)
        {
            status.Text = Loc.Instance.Tr("Connection.SignInFailed", ex.Message);
            status.Foreground = Brushes.OrangeRed;
            button.IsEnabled = true;
        }
    }

    private void OnSwitchInstanceClicked(object? sender, RoutedEventArgs e)
    {
        ConnectedPanel.IsVisible = false;
        SignInPanel.IsVisible = true;
        CancelSwitchButton.IsVisible = true;
        BaseUrlBox.Text = HaSession.Credentials?.BaseUrl ?? string.Empty;
    }

    private void OnCancelSwitchClicked(object? sender, RoutedEventArgs e) => UpdateConnectionUi();

    private async void OnSignOutClicked(object? sender, RoutedEventArgs e)
    {
        await HaSession.SignOutAsync();
        // UpdateConnectionUi() runs via the ConnectionChanged event this raises.
    }

    private bool _suppressWeatherEvents;

    private async Task LoadWeatherUiAsync()
    {
        _suppressWeatherEvents = true;

        var prefs = AppSettings.WeatherPrefs;
        WeatherEnabledCheckBox.IsChecked = prefs.Enabled;
        WeatherBackgroundCheckBox.IsChecked = prefs.ShowConditionBackground;
        WeatherWindHumidityCheckBox.IsChecked = prefs.ShowWindAndHumidity;
        WeatherForecastCheckBox.IsChecked = prefs.ShowForecast;

        var daysBox = WeatherForecastDaysBox;
        daysBox.SelectedItem = daysBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Tag == prefs.ForecastDays.ToString())
            ?? daysBox.Items.OfType<ComboBoxItem>().ElementAt(1); // "4 days" default

        var combo = WeatherEntityBox;
        combo.Items.Clear();

        if (HaSession.Client is { } client)
        {
            try
            {
                var weatherStates = await GetEntitiesOfDomainAsync(client, "weather");
                foreach (var state in weatherStates)
                {
                    var item = new ComboBoxItem { Content = HaEntityDisplay.LabelFor(state), Tag = state.EntityId };
                    combo.Items.Add(item);
                    if (state.EntityId == prefs.EntityId)
                        combo.SelectedItem = item;
                }
            }
            catch (Exception ex) { Log.Swallowed(ex); /* leave the list empty, user can retry by reopening Settings */ }
        }

        _suppressWeatherEvents = false;
    }

    private async void OnWeatherEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressWeatherEvents) return;
        await SaveWeatherPrefsAsync();
    }

    private async void OnWeatherEntityChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressWeatherEvents) return;
        await SaveWeatherPrefsAsync();
    }

    private async void OnWeatherOptionChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressWeatherEvents) return;
        await SaveWeatherPrefsAsync();
    }

    private async void OnWeatherForecastDaysChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressWeatherEvents) return;
        await SaveWeatherPrefsAsync();
    }

    private async Task SaveWeatherPrefsAsync()
    {
        var enabled = WeatherEnabledCheckBox.IsChecked == true;
        var entityId = (WeatherEntityBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var showWindAndHumidity = WeatherWindHumidityCheckBox.IsChecked == true;
        var showForecast = WeatherForecastCheckBox.IsChecked == true;
        var forecastDaysTag = (WeatherForecastDaysBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var forecastDays = int.TryParse(forecastDaysTag, out var days) ? days : 4;
        var showConditionBackground = WeatherBackgroundCheckBox.IsChecked == true;
        await AppSettings.SetWeatherPreferencesAsync(new WeatherPreferences(enabled, entityId, showWindAndHumidity, showForecast, forecastDays, showConditionBackground));
    }

    private bool _suppressMediaPlayerEvents;

    private async Task LoadMediaPlayerUiAsync()
    {
        _suppressMediaPlayerEvents = true;

        var prefs = AppSettings.MediaPlayerPrefs;
        MediaPlayerEnabledCheckBox.IsChecked = prefs.Enabled;
        MediaPlayerBackgroundCheckBox.IsChecked = prefs.UseAlbumArtBackground;

        var combo = MediaPlayerEntityBox;
        combo.Items.Clear();

        var autoItem = new ComboBoxItem { Content = Loc.Instance.Tr("Tiles.MediaAuto"), Tag = null };
        combo.Items.Add(autoItem);
        combo.SelectedItem = autoItem;

        if (HaSession.Client is { } client)
        {
            try
            {
                var mediaPlayerStates = await GetEntitiesOfDomainAsync(client, "media_player");
                foreach (var state in mediaPlayerStates)
                {
                    var item = new ComboBoxItem { Content = HaEntityDisplay.LabelFor(state), Tag = state.EntityId };
                    combo.Items.Add(item);
                    if (state.EntityId == prefs.EntityId)
                        combo.SelectedItem = item;
                }
            }
            catch (Exception ex) { Log.Swallowed(ex); /* leave the list empty except Auto — user can retry by reopening Settings */ }
        }

        _suppressMediaPlayerEvents = false;
    }

    private async void OnMediaPlayerEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressMediaPlayerEvents) return;
        await SaveMediaPlayerPrefsAsync();
    }

    private async void OnMediaPlayerEntityChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressMediaPlayerEvents) return;
        await SaveMediaPlayerPrefsAsync();
    }

    private async void OnMediaPlayerBackgroundChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressMediaPlayerEvents) return;
        await SaveMediaPlayerPrefsAsync();
    }

    private async Task SaveMediaPlayerPrefsAsync()
    {
        var enabled = MediaPlayerEnabledCheckBox.IsChecked == true;
        var entityId = (MediaPlayerEntityBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var useAlbumArtBackground = MediaPlayerBackgroundCheckBox.IsChecked == true;
        await AppSettings.SetMediaPlayerPreferencesAsync(new MediaPlayerPreferences(enabled, entityId, useAlbumArtBackground));
    }

    private void LoadAppearanceUi()
    {
        var radio = AppSettings.Appearance.Shape switch
        {
            TileShape.Square => "ShapeSquareRadio",
            TileShape.Pill => "ShapePillRadio",
            _ => "ShapeRoundedRadio",
        };
        this.FindControl<RadioButton>(radio)!.IsChecked = true;
    }

    private async void OnTileShapeChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } radio) return;

        var shape = radio.Name switch
        {
            "ShapeSquareRadio" => TileShape.Square,
            "ShapePillRadio" => TileShape.Pill,
            _ => TileShape.Rounded,
        };

        await AppSettings.SetAppearanceAsync(new AppearancePreferences(shape));
    }

    private bool _suppressLanguageEvents;

    private void LoadLanguageUi()
    {
        _suppressLanguageEvents = true;
        var box = LanguageBox;
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == AppSettings.Language.ToString())
            ?? box.Items.OfType<ComboBoxItem>().First();
        _suppressLanguageEvents = false;
    }

    private async void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageEvents) return;
        var tag = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (tag is null || !Enum.TryParse<AppLanguage>(tag, out var language)) return;

        await AppSettings.SetLanguageAsync(language);
    }

    private static readonly string[] SensorToggleNames =
    {
        "ShareCpuCheckBox", "ShareMemoryCheckBox", "ShareBatteryCheckBox", "ShareDiskCheckBox",
        "ShareUptimeCheckBox", "ShareActiveWindowCheckBox", "ShareGpuCheckBox", "ShareNetworkCheckBox",
        "ShareStorageCheckBox", "ShareDiskThroughputCheckBox", "ShareSessionLockCheckBox", "ShareVolumeCheckBox",
        "ShareActiveAudioOutputCheckBox", "ShareActiveAudioInputCheckBox", "ShareAudioOutputInUseCheckBox", "ShareAudioInputInUseCheckBox",
        "ShareActiveCameraCheckBox", "ShareCameraInUseCheckBox", "ShareSsidCheckBox", "ShareBssidCheckBox", "ShareConnectionTypeCheckBox",
        "ShareDisplayCountCheckBox", "SharePrimaryDisplayCheckBox",
    };

    // Assumed available until TestGpuAvailabilityAsync finishes, so the toggle doesn't flash
    // disabled-then-enabled on the common case where GPU sensing does work.
    private bool _gpuAvailable = true;

    /// <summary>
    /// Samples the local GPU collector to find out whether this machine can actually report GPU
    /// usage at all, rather than just assuming it can and letting the toggle silently do nothing —
    /// the "GPU Engine" performance-counter path (used for non-NVIDIA GPUs) always returns null on
    /// its very first sample by design (it has no prior value yet to diff against), so a second
    /// sample a moment later is needed before concluding it's genuinely unavailable.
    ///
    /// Each CollectAsync is wrapped in Task.Run so its synchronous Win32/perf-counter work runs on
    /// a thread-pool thread instead of this window's UI thread — opening the GPU counter query (or
    /// launching nvidia-smi) for the first time can take long enough to visibly stall the window.
    /// </summary>
    private async Task TestGpuAvailabilityAsync()
    {
        try
        {
            var gpuOnly = SensorPreferences.Default with { ShareGpu = true };
            var first = await Task.Run(() => SystemSensorCollector.Current.CollectAsync(gpuOnly));
            _gpuAvailable = first.GpuPercent is not null;

            if (!_gpuAvailable)
            {
                await Task.Delay(300);
                var second = await Task.Run(() => SystemSensorCollector.Current.CollectAsync(gpuOnly));
                _gpuAvailable = second.GpuPercent is not null;
            }
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            _gpuAvailable = false; // best effort — if sampling itself throws, treat GPU sensing as unavailable
        }

        if (!_gpuAvailable && AppSettings.SensorPrefs.ShareGpu)
        {
            ShareGpuCheckBox.IsChecked = false;
            await SaveSensorPrefsAsync();
        }

        GpuUnavailableNote.IsVisible = !_gpuAvailable;
        UpdateSensorRowsEnabled(SensorSharingMasterCheckBox.IsChecked == true);
    }

    private void LoadSensorUi()
    {
        var prefs = AppSettings.SensorPrefs;
        DeviceNameBox.Text = prefs.DeviceName;
        SensorSharingMasterCheckBox.IsChecked = prefs.Enabled;
        ShareCpuCheckBox.IsChecked = prefs.ShareCpu;
        ShareMemoryCheckBox.IsChecked = prefs.ShareMemory;
        ShareBatteryCheckBox.IsChecked = prefs.ShareBattery;
        ShareDiskCheckBox.IsChecked = prefs.ShareDisk;
        ShareStorageCheckBox.IsChecked = prefs.ShareStorage;
        ShareUptimeCheckBox.IsChecked = prefs.ShareUptime;
        ShareActiveWindowCheckBox.IsChecked = prefs.ShareActiveWindow;
        ShareGpuCheckBox.IsChecked = prefs.ShareGpu;
        ShareNetworkCheckBox.IsChecked = prefs.ShareNetwork;
        ShareDiskThroughputCheckBox.IsChecked = prefs.ShareDiskThroughput;
        ShareSessionLockCheckBox.IsChecked = prefs.ShareSessionLock;
        ShareVolumeCheckBox.IsChecked = prefs.ShareVolume;
        ShareActiveAudioOutputCheckBox.IsChecked = prefs.ShareActiveAudioOutput;
        ShareActiveAudioInputCheckBox.IsChecked = prefs.ShareActiveAudioInput;
        ShareAudioOutputInUseCheckBox.IsChecked = prefs.ShareAudioOutputInUse;
        ShareAudioInputInUseCheckBox.IsChecked = prefs.ShareAudioInputInUse;
        ShareActiveCameraCheckBox.IsChecked = prefs.ShareActiveCamera;
        ShareCameraInUseCheckBox.IsChecked = prefs.ShareCameraInUse;
        ShareSsidCheckBox.IsChecked = prefs.ShareSsid;
        ShareBssidCheckBox.IsChecked = prefs.ShareBssid;
        ShareConnectionTypeCheckBox.IsChecked = prefs.ShareConnectionType;
        ShareDisplayCountCheckBox.IsChecked = prefs.ShareDisplayCount;
        SharePrimaryDisplayCheckBox.IsChecked = prefs.SharePrimaryDisplay;
        UpdateDeviceSlugPreview(prefs.DeviceName);
        UpdateSensorRowsEnabled(prefs.Enabled);
    }

    /// <summary>Greys out the individual sensor toggles when the master switch is off, without touching their saved selections. The GPU toggle additionally stays force-disabled whenever TestGpuAvailabilityAsync found no usable GPU sensor on this machine, regardless of the master switch.</summary>
    private void UpdateSensorRowsEnabled(bool masterEnabled)
    {
        foreach (var name in SensorToggleNames)
        {
            var isGpuToggle = name == "ShareGpuCheckBox";
            this.FindControl<ToggleSwitch>(name)!.IsEnabled = masterEnabled && (!isGpuToggle || _gpuAvailable);
        }
    }

    private async void OnSensorSharingMasterChanged(object? sender, RoutedEventArgs e)
    {
        var enabled = SensorSharingMasterCheckBox.IsChecked == true;
        UpdateSensorRowsEnabled(enabled);
        await SaveSensorPrefsAsync();
    }

    private void UpdateDeviceSlugPreview(string deviceName)
    {
        DeviceSlugPreview.Text = Loc.Instance.Tr("Sensors.DeviceSlugPreview", deviceName);
    }

    private async void OnDeviceNameLostFocus(object? sender, RoutedEventArgs e) => await SaveSensorPrefsAsync();

    private async void OnSensorToggleChanged(object? sender, RoutedEventArgs e) => await SaveSensorPrefsAsync();

    private async Task SaveSensorPrefsAsync()
    {
        var deviceName = DeviceNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(deviceName)) deviceName = "HA Desktop";
        UpdateDeviceSlugPreview(deviceName);

        var prefs = new SensorPreferences(
            deviceName,
            ShareCpuCheckBox.IsChecked == true,
            ShareMemoryCheckBox.IsChecked == true,
            ShareBatteryCheckBox.IsChecked == true,
            ShareDiskCheckBox.IsChecked == true,
            ShareUptimeCheckBox.IsChecked == true,
            ShareActiveWindowCheckBox.IsChecked == true,
            ShareGpuCheckBox.IsChecked == true,
            ShareNetworkCheckBox.IsChecked == true,
            SensorSharingMasterCheckBox.IsChecked == true,
            ShareStorageCheckBox.IsChecked == true,
            ShareDiskThroughputCheckBox.IsChecked == true,
            ShareSessionLockCheckBox.IsChecked == true,
            ShareVolumeCheckBox.IsChecked == true,
            ShareActiveAudioOutputCheckBox.IsChecked == true,
            ShareActiveAudioInputCheckBox.IsChecked == true,
            ShareAudioOutputInUseCheckBox.IsChecked == true,
            ShareAudioInputInUseCheckBox.IsChecked == true,
            ShareActiveCameraCheckBox.IsChecked == true,
            ShareCameraInUseCheckBox.IsChecked == true,
            ShareSsidCheckBox.IsChecked == true,
            ShareBssidCheckBox.IsChecked == true,
            ShareConnectionTypeCheckBox.IsChecked == true,
            ShareDisplayCountCheckBox.IsChecked == true,
            SharePrimaryDisplayCheckBox.IsChecked == true);

        await AppSettings.SetSensorPreferencesAsync(prefs);
    }

    private async void OnAutostartChanged(object? sender, RoutedEventArgs e)
    {
        var checkBox = AutostartCheckBox;
        var isChecked = checkBox.IsChecked == true;

        try
        {
            await AutostartManager.Current.SetEnabledAsync(isChecked);
        }
        catch (Exception ex)
        {
            var status = StatusText;
            status.Text = Loc.Instance.Tr("Connection.StartupError", ex.Message);
            status.Foreground = Brushes.OrangeRed;
            checkBox.IsChecked = !isChecked; // revert the toggle since it didn't actually take effect
        }
    }

    private async void OnNotificationsChanged(object? sender, RoutedEventArgs e)
    {
        var isChecked = NotificationsCheckBox.IsChecked == true;
        await AppSettings.SetNotificationsEnabledAsync(isChecked);
    }

    private void LoadNotificationsUi()
    {
        NotificationsCheckBox.IsChecked = AppSettings.NotificationsEnabled;

        var slug = ApproximateHaSlug(AppSettings.SensorPrefs.DeviceName);
        NotifyServiceText.Text = Loc.Instance.Tr("Notifications.ServiceText", slug);
    }

    private static string ApproximateHaSlug(string name)
    {
        var lowered = name.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder();
        var lastWasUnderscore = false;
        foreach (var ch in lowered)
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastWasUnderscore = false; }
            else if (!lastWasUnderscore && sb.Length > 0) { sb.Append('_'); lastWasUnderscore = true; }
        }
        return sb.ToString().TrimEnd('_');
    }

    private async void OnTestNotificationClicked(object? sender, RoutedEventArgs e)
    {
        await NotificationRelay.SendTestNotificationAsync();
    }
}
