using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HaDesktop.Core.Diagnostics;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Sensors;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>Every 30 seconds, samples the local sensors the user chose to share and pushes them to Home Assistant through this machine's mobile_app device.</summary>
public static class SensorPublisher
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private static Timer? _timer;

    /// <summary>One shareable sensor: the preference that opts into it, and how to turn a snapshot into its payload (null when this machine has no reading for it).</summary>
    private sealed record Definition(Func<SensorPreferences, bool> IsShared, Func<SensorSnapshot, MobileAppSensor?> Build);

    private static readonly Definition[] Definitions =
    {
        Percent(p => p.ShareCpu, "cpu", "CPU Usage", s => s.CpuPercent, "mdi:chip"),
        Percent(p => p.ShareMemory, "memory", "Memory Usage", s => s.MemoryPercent, "mdi:memory"),
        Percent(p => p.ShareBattery, "battery", "Battery", s => s.BatteryPercent, "mdi:battery", deviceClass: "battery"),
        Percent(p => p.ShareDisk, "disk", "Disk Usage", s => s.DiskPercent, "mdi:harddisk"),
        Percent(p => p.ShareGpu, "gpu", "GPU Usage", s => s.GpuPercent, "mdi:expansion-card"),
        Percent(p => p.ShareStorage, "storage", "Storage Used", s => s.StoragePercent, "mdi:database"),
        new(p => p.ShareUptime, s => s.UptimeHours is { } hours
            ? new MobileAppSensor("uptime", "Uptime", Math.Round(hours, 1), "mdi:clock-outline", null, "h", "measurement") : null),
        DataRate(p => p.ShareNetwork, "network", "Network Throughput", s => s.NetworkMbps, "mdi:network"),
        DataRate(p => p.ShareDiskThroughput, "disk_throughput", "Disk Throughput", s => s.DiskThroughputMbps, "mdi:harddisk"),
        Text(p => p.ShareActiveWindow, "active_window", "Active Window", s => s.ActiveWindowTitle, "mdi:application-outline"),
        Flag(p => p.ShareSessionLock, "session_locked", "Session Locked", s => s.IsSessionLocked, ("Locked", "mdi:lock"), ("Unlocked", "mdi:lock-open-variant")),
        Percent(p => p.ShareVolume, "volume", "System Volume", s => s.VolumePercent, "mdi:volume-high"),
        Flag(p => p.ShareVolume, "muted", "Muted", s => s.IsMuted, ("Muted", "mdi:volume-mute"), ("Unmuted", "mdi:volume-high")),
        Text(p => p.ShareActiveAudioOutput, "active_audio_output", "Active Audio Output", s => s.ActiveAudioOutput, "mdi:speaker"),
        Text(p => p.ShareActiveAudioInput, "active_audio_input", "Active Audio Input", s => s.ActiveAudioInput, "mdi:microphone"),
        Flag(p => p.ShareAudioOutputInUse, "audio_output_in_use", "Audio Output In Use", s => s.IsAudioOutputInUse, ("In Use", "mdi:volume-high"), ("Idle", "mdi:volume-off")),
        Flag(p => p.ShareAudioInputInUse, "audio_input_in_use", "Audio Input In Use", s => s.IsAudioInputInUse, ("In Use", "mdi:microphone"), ("Idle", "mdi:microphone-off")),
        Text(p => p.ShareActiveCamera, "active_camera", "Active Camera", s => s.ActiveCamera, "mdi:camera"),
        Flag(p => p.ShareCameraInUse, "camera_in_use", "Camera In Use", s => s.IsCameraInUse, ("In Use", "mdi:camera"), ("Idle", "mdi:camera-off")),
        Text(p => p.ShareSsid, "ssid", "SSID", s => s.Ssid, "mdi:wifi"),
        Text(p => p.ShareBssid, "bssid", "BSSID", s => s.Bssid, "mdi:wifi-marker"),
        new(p => p.ShareConnectionType, s => string.IsNullOrEmpty(s.ConnectionType) ? null
            : new MobileAppSensor("connection_type", "Connection Type", s.ConnectionType, s.ConnectionType == "Wi-Fi" ? "mdi:wifi" : "mdi:ethernet")),
        new(p => p.ShareDisplayCount, s => s.DisplayCount is { } count
            ? new MobileAppSensor("displays", "Displays", count, "mdi:monitor-multiple", null, null, "measurement") : null),
        Text(p => p.SharePrimaryDisplay, "primary_display", "Primary Display", s => s.PrimaryDisplay, "mdi:monitor"),
    };

    private static Definition Percent(Func<SensorPreferences, bool> isShared, string key, string name, Func<SensorSnapshot, double?> read, string icon, string? deviceClass = null) =>
        new(isShared, s => read(s) is { } value ? new MobileAppSensor(key, name, Math.Round(value, 1), icon, deviceClass, "%", "measurement") : null);

    private static Definition DataRate(Func<SensorPreferences, bool> isShared, string key, string name, Func<SensorSnapshot, double?> read, string icon) =>
        new(isShared, s => read(s) is { } value ? new MobileAppSensor(key, name, Math.Round(value, 2), icon, "data_rate", "Mbit/s", "measurement") : null);

    private static Definition Text(Func<SensorPreferences, bool> isShared, string key, string name, Func<SensorSnapshot, string?> read, string icon) =>
        new(isShared, s => read(s) is { Length: > 0 } value ? new MobileAppSensor(key, name, value, icon) : null);

    private static Definition Flag(Func<SensorPreferences, bool> isShared, string key, string name, Func<SensorSnapshot, bool?> read,
        (string State, string Icon) whenTrue, (string State, string Icon) whenFalse) =>
        new(isShared, s => read(s) is { } value ? new MobileAppSensor(key, name, value ? whenTrue.State : whenFalse.State, value ? whenTrue.Icon : whenFalse.Icon) : null);

    /// <summary>Starts or stops the push loop to match the current connection and sensor preferences.</summary>
    public static void Update()
    {
        if (AppSettings.SensorPrefs.AnyEnabled && HaSession.Client is not null)
            _timer ??= new Timer(_ => _ = PushAsync(), null, TimeSpan.Zero, Interval);
        else
            Stop();
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private static async Task PushAsync()
    {
        var prefs = AppSettings.SensorPrefs;
        if (HaSession.Client is null || HaSession.Credentials is not { } credentials || !prefs.AnyEnabled) return;

        await HaSession.EnsureMobileAppRegisteredAsync();
        if (HaSession.Registration is not { } registration) return;

        SensorSnapshot snapshot;
        try
        {
            snapshot = await SystemSensorCollector.Current.CollectAsync(prefs);
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            return; // best effort, try again next tick
        }

        var sensors = new List<MobileAppSensor>();
        foreach (var definition in Definitions)
            if (definition.IsShared(prefs) && definition.Build(snapshot) is { } sensor) sensors.Add(sensor);

        if (sensors.Count == 0) return;

        var settings = credentials.ToConnectionSettings();
        var newSensors = sensors.Where(s => !registration.RegisteredSensorKeys.Contains(s.UniqueId)).ToList();

        foreach (var sensor in newSensors)
        {
            try
            {
                await HaSession.MobileAppClient.RegisterSensorAsync(settings, registration.WebhookId, sensor);
                registration.RegisteredSensorKeys.Add(sensor.UniqueId);
            }
            catch (MobileAppWebhookNotFoundException)
            {
                await HaSession.ForceReregisterAsync();
                return; // fresh registration has no sensors registered yet — clean slate next tick
            }
            catch (Exception ex)
            {
                Log.Swallowed(ex);
                // best effort — this one just gets retried (as a fresh registration) next tick
            }
        }

        if (newSensors.Count > 0)
            await MobileAppRegistrationStore.SaveAsync(registration);

        try
        {
            await HaSession.MobileAppClient.UpdateSensorStatesAsync(settings, registration.WebhookId, sensors);
        }
        catch (MobileAppWebhookNotFoundException)
        {
            await HaSession.ForceReregisterAsync();
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            // best effort, try again next tick
        }
    }
}
