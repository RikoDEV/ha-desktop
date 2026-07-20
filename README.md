# HA Desktop

A lightweight system tray client for [Home Assistant](https://www.home-assistant.io/), built with Avalonia UI on .NET. It lives in your tray, connects to your HA instance over the WebSocket API, and gives you quick access to entities, cameras, media players, weather, and notifications without opening a browser tab.

<img width="2560" height="1440" alt="uNaswG2y88" src="https://github.com/user-attachments/assets/8ad64ea4-d0c0-4e86-8b5a-5af3080aefbd" />

## Features

- **Quick-toggle tiles** — pin lights, switches, and other entities to the tray flyout for one-click control, with custom labels, icons, and Small/Wide sizing.
- **Visual tile layout editor** — a drag-and-drop grid editor built into Settings → Tiles: reorder tiles by dragging, resize with a click, and drop one small tile squarely onto another to stack up to 4 entities into a single 2x2 Group tile (drag one back out to split it off again).
- **Camera tiles** — live snapshots with a detail flyout.
- **Cover tiles** — open/close/stop controls for blinds, garage doors, etc.
- **Media player widget** — playback controls for a chosen media player entity.
- **Weather widget** — current conditions and forecast.
- **Sensor tiles & system sensor sharing** — optionally publish this PC's metrics back to HA as `mobile_app` sensors: CPU, memory, disk I/O activity, storage capacity used, disk throughput, GPU, battery, uptime, network throughput, active window, session lock state, and system volume/mute.
- **Remote volume control from HA** — with the volume sensor enabled, an HA automation can mute, unmute, toggle mute, or set this PC's volume by sending a `notify.mobile_app_<device>` call with a `command_volume_*` message (see [Remote commands](#remote-commands-from-ha)).
- **Native notifications** — subscribes to HA's mobile app push channel and shows notifications using the OS notification center, with an in-app history of the last 10.
- **Secure login** — signs in via HA's browser-based OAuth (loopback redirect, RFC 8252), the same flow HA's official mobile apps use. No long-lived access tokens are pasted in by hand.
- **Persistent session** — the OAuth refresh token is stored in the OS credential store (Windows Credential Manager, macOS Keychain, or the Linux equivalent) and access tokens are refreshed automatically before they expire.
- **Autostart** — optional launch on login, per-platform.
- **Cross-platform** — runs on Windows, macOS, and Linux (Linux tray support via D-Bus).

## Remote commands from HA

Most sensor data flows one way (PC → HA), but system volume is the exception: with the **System volume** sensor enabled (Settings → Sensors), HA can also control it. This isn't a separate entity or service — it piggybacks on the same `notify.mobile_app_<device_slug>` channel already used for push notifications, using a `command_*` message that the app recognizes and acts on locally instead of showing a notification.

Mute, unmute, or toggle mute:

```yaml
service: notify.mobile_app_<device_slug>
data:
  message: "command_volume_mute"        # or command_volume_unmute / command_volume_toggle_mute
```

Set an exact volume level (0-100):

```yaml
service: notify.mobile_app_<device_slug>
data:
  message: "command_volume_set"
  data:
    volume_level: 40
```

`<device_slug>` is the device name set in Settings → Sensors, slugified (shown right below that field in the app). Currently implemented on Windows only.

## Architecture

The solution is split into two projects:

| Project | Purpose |
|---|---|
| `HaDesktop.Core` | Platform-agnostic library: HA WebSocket/REST client, OAuth login, mobile_app registration, credential storage, sensor collection, preferences persistence, notifications, and autostart — each with per-OS implementations behind an interface (`*Manager`/`*Store`/`*Collector`/`*Notifier`), selected at runtime via a `.Current` property. |
| `HaDesktop.Tray` | The Avalonia tray application: tray icon, flyout window, tiles, and settings UI. |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A running Home Assistant instance reachable from this machine

## Building & running

```bash
dotnet build HaDesktop.sln
dotnet run --project src/HaDesktop.Tray
```

On first run, click the tray icon and sign in with your Home Assistant URL — this opens your browser for HA's login page and completes the OAuth loopback flow automatically.

## Releases

Tagged pushes (`vX.Y.Z`) trigger [`.github/workflows/release.yml`](.github/workflows/release.yml), which builds and attaches to a GitHub Release:

- **Windows** — `HaDesktop-Setup-X.Y.Z.exe` (Inno Setup installer, per-user install, no admin required) and a `HaDesktop-X.Y.Z-win-x64-portable.zip` (self-contained, no install needed).
- **macOS** — `HaDesktop-X.Y.Z-osx-x64.zip` / `-osx-arm64.zip`, each a self-contained `.app` bundle.
- **Linux** — `HaDesktop-X.Y.Z-linux-x64-portable.tar.gz` (self-contained single-file binary).

The workflow can also be run manually (`workflow_dispatch`) to sanity-check packaging without cutting a release.

To build the Windows installer locally, publish a self-contained `win-x64` build to `publish/win-x64` and run [Inno Setup](https://jrsoftware.org/isinfo.php)'s `ISCC.exe` against [`installer/windows/setup.iss`](installer/windows/setup.iss).

## Project layout

```
src/
  HaDesktop.Core/
    Autostart/       # Launch-on-login, per OS
    Ha/               # HA WebSocket client, OAuth login, mobile_app API client
    Notifications/    # Native OS notification wrappers
    Sensors/          # System metrics collection (CPU/mem/disk/GPU/etc.), per OS
    Storage/          # Credential store + local JSON preference stores
  HaDesktop.Tray/
    *.axaml(.cs)      # Tray flyout, tiles, and settings windows (Avalonia)
    AppSettings.cs     # App-wide session/connection state and background timers
    Program.cs         # Entry point
installer/
  windows/
    setup.iss          # Inno Setup script for the Windows installer
.github/workflows/
  ci.yml                # Build check on every push/PR
  release.yml            # Builds installer + portable packages and publishes a GitHub Release on tag push
```

## License

[MIT](LICENSE)
