using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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

        _settingsMenuItem = new NativeMenuItem { Command = new RelayCommand(OpenSettings), Icon = RenderMenuIcon("cog") };
        _quitMenuItem = new NativeMenuItem { Command = new RelayCommand(() => desktop.Shutdown()), Icon = RenderMenuIcon("power") };

        var menu = new NativeMenu
        {
            Items =
            {
                _settingsMenuItem,
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
        _quitMenuItem!.Header = Loc.Instance.Tr("Tray.Quit");
    }

    /// <summary>
    /// NativeMenuItem.Icon wants an actual Bitmap, not a vector PathIcon like the rest of the app
    /// uses — this rasterizes one of TileIcons' path strings into a small one instead of shipping
    /// separate bitmap assets just for the two tray menu entries. A fixed mid-gray rather than a
    /// theme-following color: native context menus follow the OS light/dark setting regardless of
    /// this app, and there's no cross-platform way to get an auto-tinting "template image" (macOS's
    /// own mechanism for this) through Avalonia's NativeMenuItem — mid-gray reads acceptably on
    /// both a light and a dark menu background, which pure black or white wouldn't.
    /// </summary>
    private static Bitmap RenderMenuIcon(string iconKey, int size = 16)
    {
        var geometry = Geometry.Parse(TileIcons.PathFor(iconKey));
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        using (ctx.PushTransform(Matrix.CreateScale(size / 24.0, size / 24.0)))
        {
            ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#767676")), null, geometry);
        }
        return bitmap;
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
