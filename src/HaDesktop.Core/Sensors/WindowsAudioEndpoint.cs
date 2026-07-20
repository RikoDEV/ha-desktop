using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HaDesktop.Core.Sensors;

/// <summary>
/// Shared Windows Core Audio (IAudioEndpointVolume) COM interop for the default playback device —
/// there's no managed wrapper for this in the BCL, so it's raw COM interop against a handful of
/// well-known, ABI-stable interfaces (unchanged since Vista). Used both to read the volume/mute
/// sensor (<see cref="WindowsSensorCollector"/>) and to act on HA's reverse "command_volume_*"
/// notifications (<see cref="WindowsAudioController"/>) — kept in one place so the GUIDs/vtable
/// declarations exist exactly once. Any failure (no default audio endpoint, COM activation failure,
/// etc.) degrades to a no-op/null result rather than throwing.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAudioEndpoint
{
    public static (double? VolumePercent, bool? IsMuted) GetState()
    {
        return WithEndpointVolume(endpointVolume =>
        {
            endpointVolume.GetMasterVolumeLevelScalar(out var level);
            endpointVolume.GetMute(out var muted);
            return ((double?)Math.Round(level * 100.0, 0), (bool?)muted);
        }) ?? (null, null);
    }

    public static bool SetMute(bool muted)
    {
        var eventContext = Guid.Empty;
        return WithEndpointVolume(endpointVolume =>
        {
            endpointVolume.SetMute(muted, ref eventContext);
            return true;
        }) ?? false;
    }

    public static bool SetVolumePercent(double percent)
    {
        var level = (float)(Math.Clamp(percent, 0, 100) / 100.0);
        var eventContext = Guid.Empty;
        return WithEndpointVolume(endpointVolume =>
        {
            endpointVolume.SetMasterVolumeLevelScalar(level, ref eventContext);
            return true;
        }) ?? false;
    }

    private static T? WithEndpointVolume<T>(Func<IAudioEndpointVolume, T> action) where T : struct
    {
        object? enumeratorObj = null;
        IMMDevice? device = null;
        object? endpointVolumeObj = null;
        try
        {
            enumeratorObj = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObj;
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device);

            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out endpointVolumeObj);
            var endpointVolume = (IAudioEndpointVolume)endpointVolumeObj;

            return action(endpointVolume);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (endpointVolumeObj is not null) Marshal.ReleaseComObject(endpointVolumeObj);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumeratorObj is not null) Marshal.ReleaseComObject(enumeratorObj);
        }
    }

    private const int ClsCtxAll = 23;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out int channelCount);
        int SetMasterVolumeLevel(float level, ref Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int GetMasterVolumeLevel(out float level);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        int GetChannelVolumeLevel(uint channel, out float level);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
    }
}
