using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HaDesktop.Core.Sensors;

/// <summary>Reverse of <see cref="ISystemSensorCollector"/>'s volume/mute reading — lets HA (via a
/// "command_volume_*" push notification, see AppSettings.OnRemoteCommandReceived) actually change
/// this machine's volume instead of only observing it.</summary>
public interface ISystemAudioController
{
    /// <summary>Returns whether the change was actually applied — false if this OS/device has no controllable audio endpoint.</summary>
    bool SetMute(bool muted);

    /// <summary>Returns whether the change was actually applied — false if this OS/device has no controllable audio endpoint.</summary>
    bool SetVolumePercent(double percent);

    /// <summary>Current mute state, if it can be determined — used to implement "toggle mute" without a separate read path per platform.</summary>
    bool? GetMuted();
}

public static class SystemAudioController
{
    public static ISystemAudioController Current { get; } = Create();

    private static ISystemAudioController Create() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new WindowsAudioController() : new NullAudioController();
}

/// <summary>No-op fallback for platforms without a volume-control implementation yet (Linux/macOS) — matches the rest of the sensor stack's "best effort" degradation instead of throwing.</summary>
internal sealed class NullAudioController : ISystemAudioController
{
    public bool SetMute(bool muted) => false;
    public bool SetVolumePercent(double percent) => false;
    public bool? GetMuted() => null;
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsAudioController : ISystemAudioController
{
    public bool SetMute(bool muted) => WindowsAudioEndpoint.SetMute(muted);
    public bool SetVolumePercent(double percent) => WindowsAudioEndpoint.SetVolumePercent(percent);
    public bool? GetMuted() => WindowsAudioEndpoint.GetState().IsMuted;
}
