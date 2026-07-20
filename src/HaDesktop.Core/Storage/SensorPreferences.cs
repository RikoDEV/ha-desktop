namespace HaDesktop.Core.Storage;

public sealed record SensorPreferences(
    string DeviceName,
    bool ShareCpu,
    bool ShareMemory,
    bool ShareBattery,
    bool ShareDisk,
    bool ShareUptime,
    bool ShareActiveWindow,
    bool ShareGpu,
    bool ShareNetwork,
    bool Enabled,
    bool ShareStorage = false,
    bool ShareDiskThroughput = false,
    bool ShareSessionLock = false,
    bool ShareVolume = false,
    bool ShareActiveAudioOutput = false,
    bool ShareActiveAudioInput = false,
    bool ShareAudioOutputInUse = false,
    bool ShareAudioInputInUse = false,
    bool ShareActiveCamera = false,
    bool ShareCameraInUse = false,
    bool ShareSsid = false,
    bool ShareBssid = false,
    bool ShareConnectionType = false,
    bool ShareDisplayCount = false,
    bool SharePrimaryDisplay = false)
{
    public static SensorPreferences Default { get; } = new(
        "HA Desktop", false, false, false, false, false, false, false, false, false,
        false, false, false, false, false, false, false, false, false, false, false, false, false, false);

    /// <summary>Master switch AND at least one individual sensor selected — flipping the master off stops sharing without losing which sensors were picked.</summary>
    public bool AnyEnabled => Enabled &&
        (ShareCpu || ShareMemory || ShareBattery || ShareDisk || ShareUptime || ShareActiveWindow || ShareGpu || ShareNetwork
            || ShareStorage || ShareDiskThroughput || ShareSessionLock || ShareVolume
            || ShareActiveAudioOutput || ShareActiveAudioInput || ShareAudioOutputInUse || ShareAudioInputInUse
            || ShareActiveCamera || ShareCameraInUse || ShareSsid || ShareBssid || ShareConnectionType
            || ShareDisplayCount || SharePrimaryDisplay);
}
