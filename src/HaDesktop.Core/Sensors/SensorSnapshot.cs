namespace HaDesktop.Core.Sensors;

/// <summary>A single reading of this machine's local metrics. Any field can be null if that OS/collector didn't report it.</summary>
public sealed record SensorSnapshot(
    double? CpuPercent,
    double? MemoryPercent,
    double? BatteryPercent,
    double? DiskPercent,
    double? UptimeHours,
    string? ActiveWindowTitle,
    double? GpuPercent,
    double? NetworkMbps,
    double? StoragePercent = null,
    double? DiskThroughputMbps = null,
    bool? IsSessionLocked = null,
    double? VolumePercent = null,
    bool? IsMuted = null,
    string? ActiveAudioOutput = null,
    string? ActiveAudioInput = null,
    bool? IsAudioOutputInUse = null,
    bool? IsAudioInputInUse = null,
    string? ActiveCamera = null,
    bool? IsCameraInUse = null,
    string? Ssid = null,
    string? Bssid = null,
    string? ConnectionType = null,
    int? DisplayCount = null,
    string? PrimaryDisplay = null);
