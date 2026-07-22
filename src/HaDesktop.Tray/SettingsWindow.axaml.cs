using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using HaDesktop.Core.Autostart;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Sensors;
using HaDesktop.Core.Storage;
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
            this.FindControl<PathIcon>(iconName)!.Data = Geometry.Parse(TileIcons.PathFor(iconKey));

        this.FindControl<ContentControl>("TileEditorHost")!.Content = new TileLayoutEditor();

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
        this.FindControl<ListBox>("NavList")!.SelectedIndex = 0;

        AppSettings.ConnectionChanged += OnConnectionChanged;
        Loc.Instance.LanguageChanged += OnLanguageChangedRefresh;
        Closed += (_, _) =>
        {
            AppSettings.ConnectionChanged -= OnConnectionChanged;
            Loc.Instance.LanguageChanged -= OnLanguageChangedRefresh;
        };
        _ = RefreshUpdatesThenInstanceInfoAsync();
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
        this.FindControl<PathIcon>("AboutAppIcon")!.Data = Geometry.Parse(TileIcons.PathFor("cover"));

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        this.FindControl<TextBlock>("AboutVersionText")!.Text = version is null ? Loc.Instance.Tr("About.DevelopmentBuild") : version.ToString(3);
    }

    private void OnAuthorLinkClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://riko.dev") { UseShellExecute = true });
        }
        catch { /* best effort — no default browser handler, nothing sensible to do */ }
    }

    private void OnGitHubLinkClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/RikoDEV/ha-desktop") { UseShellExecute = true });
        }
        catch { /* best effort — no default browser handler, nothing sensible to do */ }
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selectedIndex = this.FindControl<ListBox>("NavList")!.SelectedIndex;
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
        UpdateDeviceSlugPreview(this.FindControl<TextBox>("DeviceNameBox")!.Text?.Trim() is { Length: > 0 } name ? name : AppSettings.SensorPrefs.DeviceName);
        _ = RefreshUpdatesThenInstanceInfoAsync();
    });

    private int _updatesRefreshToken;

    private async Task RefreshUpdatesAsync()
    {
        var myToken = ++_updatesRefreshToken;
        var card = this.FindControl<Border>("UpdatesCard")!;
        var panel = this.FindControl<StackPanel>("UpdatesPanel")!;

        if (AppSettings.Client is not { } client)
        {
            card.IsVisible = false;
            return;
        }

        List<HaEntityState> states;
        try
        {
            states = await client.GetStatesAsync();
        }
        catch
        {
            if (myToken == _updatesRefreshToken) card.IsVisible = false;
            return;
        }

        if (myToken != _updatesRefreshToken) return; // superseded by a later call while we were awaiting

        // Match HA's own Updates view: only entities with a pending update (state "on"),
        // not the full list of update.* entities (most of which are just up to date).
        var pending = states.Where(s => s.Domain == "update" && s.State == "on")
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
                    await AppSettings.Client!.CallServiceAsync("update", "install", entityId);
                }
                catch
                {
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
        var card = this.FindControl<Border>("InstanceInfoCard")!;
        var panel = this.FindControl<StackPanel>("InstanceInfoPanel")!;

        if (AppSettings.Client is not { } client)
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
        catch
        {
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
        var isConnected = AppSettings.Client is not null && AppSettings.Credentials is not null;

        this.FindControl<StackPanel>("ConnectedPanel")!.IsVisible = isConnected;
        this.FindControl<StackPanel>("SignInPanel")!.IsVisible = !isConnected;
        this.FindControl<Button>("CancelSwitchButton")!.IsVisible = false;

        if (isConnected)
        {
            this.FindControl<TextBlock>("ConnectedUrlText")!.Text = Loc.Instance.Tr("Connection.ConnectedTo", AppSettings.Credentials!.BaseUrl);
        }
        else
        {
            this.FindControl<TextBox>("BaseUrlBox")!.Text = string.Empty;
            this.FindControl<TextBlock>("StatusText")!.Text = string.Empty;
            this.FindControl<Button>("LoginButton")!.IsEnabled = true;
        }
    }

    private async Task LoadAutostartStateAsync()
    {
        var isEnabled = await AutostartManager.Current.IsEnabledAsync();
        this.FindControl<ToggleSwitch>("AutostartCheckBox")!.IsChecked = isEnabled;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // Matches BaseUrlBox's watermark — if the user hits sign-in without typing a URL,
    // assume the common local mDNS address rather than blocking on an empty field.
    private const string DefaultBaseUrl = "http://homeassistant.local:8123";

    private async void OnLoginClicked(object? sender, RoutedEventArgs e)
    {
        var status = this.FindControl<TextBlock>("StatusText")!;
        var button = this.FindControl<Button>("LoginButton")!;
        var baseUrl = this.FindControl<TextBox>("BaseUrlBox")!.Text?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = DefaultBaseUrl;

        button.IsEnabled = false;
        status.Text = Loc.Instance.Tr("Connection.OpeningBrowser");
        status.Foreground = Brushes.Gray;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var credentials = await HaOAuthLogin.LoginAsync(baseUrl, cts.Token);

            status.Text = Loc.Instance.Tr("Connection.Connecting");
            await AppSettings.ConnectWithOAuthAsync(credentials);
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
        this.FindControl<StackPanel>("ConnectedPanel")!.IsVisible = false;
        this.FindControl<StackPanel>("SignInPanel")!.IsVisible = true;
        this.FindControl<Button>("CancelSwitchButton")!.IsVisible = true;
        this.FindControl<TextBox>("BaseUrlBox")!.Text = AppSettings.Credentials?.BaseUrl ?? string.Empty;
    }

    private void OnCancelSwitchClicked(object? sender, RoutedEventArgs e) => UpdateConnectionUi();

    private async void OnSignOutClicked(object? sender, RoutedEventArgs e)
    {
        await AppSettings.SignOutAsync();
        // UpdateConnectionUi() runs via the ConnectionChanged event this raises.
    }

    private bool _suppressWeatherEvents;

    private async Task LoadWeatherUiAsync()
    {
        _suppressWeatherEvents = true;

        var prefs = AppSettings.WeatherPrefs;
        this.FindControl<ToggleSwitch>("WeatherEnabledCheckBox")!.IsChecked = prefs.Enabled;
        this.FindControl<ToggleSwitch>("WeatherBackgroundCheckBox")!.IsChecked = prefs.ShowConditionBackground;
        this.FindControl<ToggleSwitch>("WeatherWindHumidityCheckBox")!.IsChecked = prefs.ShowWindAndHumidity;
        this.FindControl<ToggleSwitch>("WeatherForecastCheckBox")!.IsChecked = prefs.ShowForecast;

        var daysBox = this.FindControl<ComboBox>("WeatherForecastDaysBox")!;
        daysBox.SelectedItem = daysBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Tag == prefs.ForecastDays.ToString())
            ?? daysBox.Items.OfType<ComboBoxItem>().ElementAt(1); // "4 days" default

        var combo = this.FindControl<ComboBox>("WeatherEntityBox")!;
        combo.Items.Clear();

        if (AppSettings.Client is { } client)
        {
            try
            {
                var weatherStates = (await client.GetStatesAsync()).Where(s => s.Domain == "weather");
                foreach (var state in weatherStates)
                {
                    var item = new ComboBoxItem { Content = HaEntityDisplay.LabelFor(state), Tag = state.EntityId };
                    combo.Items.Add(item);
                    if (state.EntityId == prefs.EntityId)
                        combo.SelectedItem = item;
                }
            }
            catch { /* leave the list empty, user can retry by reopening Settings */ }
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
        var enabled = this.FindControl<ToggleSwitch>("WeatherEnabledCheckBox")!.IsChecked == true;
        var entityId = (this.FindControl<ComboBox>("WeatherEntityBox")!.SelectedItem as ComboBoxItem)?.Tag as string;
        var showWindAndHumidity = this.FindControl<ToggleSwitch>("WeatherWindHumidityCheckBox")!.IsChecked == true;
        var showForecast = this.FindControl<ToggleSwitch>("WeatherForecastCheckBox")!.IsChecked == true;
        var forecastDaysTag = (this.FindControl<ComboBox>("WeatherForecastDaysBox")!.SelectedItem as ComboBoxItem)?.Tag as string;
        var forecastDays = int.TryParse(forecastDaysTag, out var days) ? days : 4;
        var showConditionBackground = this.FindControl<ToggleSwitch>("WeatherBackgroundCheckBox")!.IsChecked == true;
        await AppSettings.SetWeatherPreferencesAsync(new WeatherPreferences(enabled, entityId, showWindAndHumidity, showForecast, forecastDays, showConditionBackground));
    }

    private bool _suppressMediaPlayerEvents;

    private async Task LoadMediaPlayerUiAsync()
    {
        _suppressMediaPlayerEvents = true;

        var prefs = AppSettings.MediaPlayerPrefs;
        this.FindControl<ToggleSwitch>("MediaPlayerEnabledCheckBox")!.IsChecked = prefs.Enabled;
        this.FindControl<ToggleSwitch>("MediaPlayerBackgroundCheckBox")!.IsChecked = prefs.UseAlbumArtBackground;

        var combo = this.FindControl<ComboBox>("MediaPlayerEntityBox")!;
        combo.Items.Clear();

        var autoItem = new ComboBoxItem { Content = Loc.Instance.Tr("Tiles.MediaAuto"), Tag = null };
        combo.Items.Add(autoItem);
        combo.SelectedItem = autoItem;

        if (AppSettings.Client is { } client)
        {
            try
            {
                var mediaPlayerStates = (await client.GetStatesAsync()).Where(s => s.Domain == "media_player");
                foreach (var state in mediaPlayerStates)
                {
                    var item = new ComboBoxItem { Content = HaEntityDisplay.LabelFor(state), Tag = state.EntityId };
                    combo.Items.Add(item);
                    if (state.EntityId == prefs.EntityId)
                        combo.SelectedItem = item;
                }
            }
            catch { /* leave the list empty except Auto — user can retry by reopening Settings */ }
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
        var enabled = this.FindControl<ToggleSwitch>("MediaPlayerEnabledCheckBox")!.IsChecked == true;
        var entityId = (this.FindControl<ComboBox>("MediaPlayerEntityBox")!.SelectedItem as ComboBoxItem)?.Tag as string;
        var useAlbumArtBackground = this.FindControl<ToggleSwitch>("MediaPlayerBackgroundCheckBox")!.IsChecked == true;
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
        var box = this.FindControl<ComboBox>("LanguageBox")!;
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == AppSettings.Language.ToString())
            ?? box.Items.OfType<ComboBoxItem>().First();
        _suppressLanguageEvents = false;
    }

    private async void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageEvents) return;
        var tag = (this.FindControl<ComboBox>("LanguageBox")!.SelectedItem as ComboBoxItem)?.Tag as string;
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
    /// Each CollectAsync is wrapped in Task.Run so its synchronous Win32/perf-counter work (disk
    /// and GPU-Engine PerformanceCounter creation in particular) runs on a thread-pool thread
    /// instead of resuming on this window's UI thread after CollectAsync's own first await — the
    /// very first PerformanceCounter ever constructed in the process has to build perflib's
    /// counter/help name tables from the registry, a one-time cost of hundreds of ms that would
    /// otherwise freeze the Settings window for a moment the first time it's opened.
    /// </summary>
    private async Task TestGpuAvailabilityAsync()
    {
        try
        {
            var first = await Task.Run(() => SystemSensorCollector.Current.CollectAsync());
            _gpuAvailable = first.GpuPercent is not null;

            if (!_gpuAvailable)
            {
                await Task.Delay(300);
                var second = await Task.Run(() => SystemSensorCollector.Current.CollectAsync());
                _gpuAvailable = second.GpuPercent is not null;
            }
        }
        catch
        {
            _gpuAvailable = false; // best effort — if sampling itself throws, treat GPU sensing as unavailable
        }

        if (!_gpuAvailable && AppSettings.SensorPrefs.ShareGpu)
        {
            this.FindControl<ToggleSwitch>("ShareGpuCheckBox")!.IsChecked = false;
            await SaveSensorPrefsAsync();
        }

        this.FindControl<TextBlock>("GpuUnavailableNote")!.IsVisible = !_gpuAvailable;
        UpdateSensorRowsEnabled(this.FindControl<ToggleSwitch>("SensorSharingMasterCheckBox")!.IsChecked == true);
    }

    private void LoadSensorUi()
    {
        var prefs = AppSettings.SensorPrefs;
        this.FindControl<TextBox>("DeviceNameBox")!.Text = prefs.DeviceName;
        this.FindControl<ToggleSwitch>("SensorSharingMasterCheckBox")!.IsChecked = prefs.Enabled;
        this.FindControl<ToggleSwitch>("ShareCpuCheckBox")!.IsChecked = prefs.ShareCpu;
        this.FindControl<ToggleSwitch>("ShareMemoryCheckBox")!.IsChecked = prefs.ShareMemory;
        this.FindControl<ToggleSwitch>("ShareBatteryCheckBox")!.IsChecked = prefs.ShareBattery;
        this.FindControl<ToggleSwitch>("ShareDiskCheckBox")!.IsChecked = prefs.ShareDisk;
        this.FindControl<ToggleSwitch>("ShareStorageCheckBox")!.IsChecked = prefs.ShareStorage;
        this.FindControl<ToggleSwitch>("ShareUptimeCheckBox")!.IsChecked = prefs.ShareUptime;
        this.FindControl<ToggleSwitch>("ShareActiveWindowCheckBox")!.IsChecked = prefs.ShareActiveWindow;
        this.FindControl<ToggleSwitch>("ShareGpuCheckBox")!.IsChecked = prefs.ShareGpu;
        this.FindControl<ToggleSwitch>("ShareNetworkCheckBox")!.IsChecked = prefs.ShareNetwork;
        this.FindControl<ToggleSwitch>("ShareDiskThroughputCheckBox")!.IsChecked = prefs.ShareDiskThroughput;
        this.FindControl<ToggleSwitch>("ShareSessionLockCheckBox")!.IsChecked = prefs.ShareSessionLock;
        this.FindControl<ToggleSwitch>("ShareVolumeCheckBox")!.IsChecked = prefs.ShareVolume;
        this.FindControl<ToggleSwitch>("ShareActiveAudioOutputCheckBox")!.IsChecked = prefs.ShareActiveAudioOutput;
        this.FindControl<ToggleSwitch>("ShareActiveAudioInputCheckBox")!.IsChecked = prefs.ShareActiveAudioInput;
        this.FindControl<ToggleSwitch>("ShareAudioOutputInUseCheckBox")!.IsChecked = prefs.ShareAudioOutputInUse;
        this.FindControl<ToggleSwitch>("ShareAudioInputInUseCheckBox")!.IsChecked = prefs.ShareAudioInputInUse;
        this.FindControl<ToggleSwitch>("ShareActiveCameraCheckBox")!.IsChecked = prefs.ShareActiveCamera;
        this.FindControl<ToggleSwitch>("ShareCameraInUseCheckBox")!.IsChecked = prefs.ShareCameraInUse;
        this.FindControl<ToggleSwitch>("ShareSsidCheckBox")!.IsChecked = prefs.ShareSsid;
        this.FindControl<ToggleSwitch>("ShareBssidCheckBox")!.IsChecked = prefs.ShareBssid;
        this.FindControl<ToggleSwitch>("ShareConnectionTypeCheckBox")!.IsChecked = prefs.ShareConnectionType;
        this.FindControl<ToggleSwitch>("ShareDisplayCountCheckBox")!.IsChecked = prefs.ShareDisplayCount;
        this.FindControl<ToggleSwitch>("SharePrimaryDisplayCheckBox")!.IsChecked = prefs.SharePrimaryDisplay;
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
        var enabled = this.FindControl<ToggleSwitch>("SensorSharingMasterCheckBox")!.IsChecked == true;
        UpdateSensorRowsEnabled(enabled);
        await SaveSensorPrefsAsync();
    }

    private void UpdateDeviceSlugPreview(string deviceName)
    {
        this.FindControl<TextBlock>("DeviceSlugPreview")!.Text = Loc.Instance.Tr("Sensors.DeviceSlugPreview", deviceName);
    }

    private async void OnDeviceNameLostFocus(object? sender, RoutedEventArgs e) => await SaveSensorPrefsAsync();

    private async void OnSensorToggleChanged(object? sender, RoutedEventArgs e) => await SaveSensorPrefsAsync();

    private async Task SaveSensorPrefsAsync()
    {
        var deviceName = this.FindControl<TextBox>("DeviceNameBox")!.Text?.Trim();
        if (string.IsNullOrWhiteSpace(deviceName)) deviceName = "HA Desktop";
        UpdateDeviceSlugPreview(deviceName);

        var prefs = new SensorPreferences(
            deviceName,
            this.FindControl<ToggleSwitch>("ShareCpuCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareMemoryCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareBatteryCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareDiskCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareUptimeCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareActiveWindowCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareGpuCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareNetworkCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("SensorSharingMasterCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareStorageCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareDiskThroughputCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareSessionLockCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareVolumeCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareActiveAudioOutputCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareActiveAudioInputCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareAudioOutputInUseCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareAudioInputInUseCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareActiveCameraCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareCameraInUseCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareSsidCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareBssidCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareConnectionTypeCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("ShareDisplayCountCheckBox")!.IsChecked == true,
            this.FindControl<ToggleSwitch>("SharePrimaryDisplayCheckBox")!.IsChecked == true);

        await AppSettings.SetSensorPreferencesAsync(prefs);
    }

    private async void OnAutostartChanged(object? sender, RoutedEventArgs e)
    {
        var checkBox = this.FindControl<ToggleSwitch>("AutostartCheckBox")!;
        var isChecked = checkBox.IsChecked == true;

        try
        {
            await AutostartManager.Current.SetEnabledAsync(isChecked);
        }
        catch (Exception ex)
        {
            var status = this.FindControl<TextBlock>("StatusText")!;
            status.Text = Loc.Instance.Tr("Connection.StartupError", ex.Message);
            status.Foreground = Brushes.OrangeRed;
            checkBox.IsChecked = !isChecked; // revert the toggle since it didn't actually take effect
        }
    }

    private async void OnNotificationsChanged(object? sender, RoutedEventArgs e)
    {
        var isChecked = this.FindControl<ToggleSwitch>("NotificationsCheckBox")!.IsChecked == true;
        await AppSettings.SetNotificationsEnabledAsync(isChecked);
    }

    private void LoadNotificationsUi()
    {
        this.FindControl<ToggleSwitch>("NotificationsCheckBox")!.IsChecked = AppSettings.NotificationsEnabled;

        var slug = ApproximateHaSlug(AppSettings.SensorPrefs.DeviceName);
        this.FindControl<TextBlock>("NotifyServiceText")!.Text = Loc.Instance.Tr("Notifications.ServiceText", slug);
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
        await AppSettings.SendTestNotificationAsync();
    }
}
