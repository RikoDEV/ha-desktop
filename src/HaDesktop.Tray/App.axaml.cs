using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private FlyoutWindow? _flyoutWindow;
    private SettingsWindow? _settingsWindow;
    private NativeMenuItem? _settingsMenuItem;
    private NativeMenuItem? _signOutMenuItem;
    private NativeMenuItem? _quitMenuItem;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // No main window — the app lives entirely in the tray until the flyout or
            // settings window is explicitly opened.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _flyoutWindow = new FlyoutWindow();
            _flyoutWindow.OpenSettingsRequested += OpenSettings;
            SetupTrayIcon(desktop);

            _ = AppSettings.LoadLocalPreferencesAsync();
            _ = AppSettings.TryRestoreAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var iconBitmap = new Bitmap(AssetLoader.Open(new Uri("avares://HaDesktop.Tray/Assets/tray-icon.ico")));

        _settingsMenuItem = new NativeMenuItem { Command = new RelayCommand(OpenSettings) };
        _signOutMenuItem = new NativeMenuItem { Command = new RelayCommand(() => _ = AppSettings.SignOutAsync()) };
        _quitMenuItem = new NativeMenuItem { Command = new RelayCommand(() => desktop.Shutdown()) };

        var menu = new NativeMenu
        {
            Items =
            {
                _settingsMenuItem,
                _signOutMenuItem,
                new NativeMenuItemSeparator(),
                _quitMenuItem,
            },
        };

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconBitmap),
            ToolTipText = Loc.Instance.Tr("Tray.Tooltip"),
            Menu = menu,
        };
        _trayIcon.Clicked += (_, _) => _flyoutWindow!.ToggleVisibility();

        UpdateTrayTexts();
        Loc.Instance.LanguageChanged += UpdateTrayTexts;
    }

    private void UpdateTrayTexts()
    {
        _settingsMenuItem!.Header = Loc.Instance.Tr("Tray.Settings");
        _signOutMenuItem!.Header = Loc.Instance.Tr("Tray.SignOut");
        _quitMenuItem!.Header = Loc.Instance.Tr("Tray.Quit");
    }

    private void OpenSettings()
    {
        if (_settingsWindow is null || !_settingsWindow.IsVisible)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Show();
        }
        _settingsWindow.Activate();
    }
}
