using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace HaDesktop.Tray;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private FlyoutWindow? _flyoutWindow;
    private SettingsWindow? _settingsWindow;

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

        var menu = new NativeMenu
        {
            Items =
            {
                new NativeMenuItem("Settings") { Command = new RelayCommand(OpenSettings) },
                new NativeMenuItem("Sign Out") { Command = new RelayCommand(() => _ = AppSettings.SignOutAsync()) },
                new NativeMenuItemSeparator(),
                new NativeMenuItem("Quit") { Command = new RelayCommand(() => desktop.Shutdown()) },
            },
        };

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconBitmap),
            ToolTipText = "HA Desktop",
            Menu = menu,
        };
        _trayIcon.Clicked += (_, _) => _flyoutWindow!.ToggleVisibility();
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
