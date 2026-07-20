using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HaDesktop.Core.Sensors;

/// <summary>
/// Shared Windows Core Audio COM interop for the default playback/recording endpoints — there's no
/// managed wrapper for any of this in the BCL, so it's raw COM interop against a handful of
/// well-known, ABI-stable interfaces (unchanged since Vista): IAudioEndpointVolume (volume/mute,
/// read+write), IPropertyStore (the device's friendly name), and IAudioMeterInformation (whether
/// the render endpoint currently has an active signal, i.e. "Audio Output In Use" — this one only
/// applies to output; a nonzero *input* peak just means there's ambient sound, not that anything is
/// actually recording, so mic-in-use is answered by the privacy consent store instead, see
/// <see cref="WindowsPrivacyConsentStore"/>). Any failure (no default endpoint for that direction,
/// COM activation failure, etc.) degrades to a no-op/null result rather than throwing.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAudioEndpoint
{
    public static (double? VolumePercent, bool? IsMuted) GetState()
    {
        return WithInterface<IAudioEndpointVolume, (double?, bool?)>(EDataFlow.eRender, endpointVolume =>
        {
            endpointVolume.GetMasterVolumeLevelScalar(out var level);
            endpointVolume.GetMute(out var muted);
            return (Math.Round(level * 100.0, 0), muted);
        }) ?? (null, null);
    }

    public static bool SetMute(bool muted)
    {
        var eventContext = Guid.Empty;
        return WithInterface<IAudioEndpointVolume, bool>(EDataFlow.eRender, endpointVolume =>
        {
            endpointVolume.SetMute(muted, ref eventContext);
            return true;
        }) ?? false;
    }

    public static bool SetVolumePercent(double percent)
    {
        var level = (float)(Math.Clamp(percent, 0, 100) / 100.0);
        var eventContext = Guid.Empty;
        return WithInterface<IAudioEndpointVolume, bool>(EDataFlow.eRender, endpointVolume =>
        {
            endpointVolume.SetMasterVolumeLevelScalar(level, ref eventContext);
            return true;
        }) ?? false;
    }

    /// <summary>Friendly name of the default playback device (e.g. "Speakers (Realtek Audio)"), or null if there isn't one.</summary>
    public static string? GetOutputDeviceName() => GetDeviceName(EDataFlow.eRender);

    /// <summary>Friendly name of the default recording device (e.g. "Microphone (Realtek Audio)"), or null if there isn't one.</summary>
    public static string? GetInputDeviceName() => GetDeviceName(EDataFlow.eCapture);

    /// <summary>True if the default playback device currently has an active (non-silent) signal — i.e. something is actually playing sound right now.</summary>
    public static bool? IsOutputActive()
    {
        return WithInterface<IAudioMeterInformation, bool>(EDataFlow.eRender, meter =>
        {
            meter.GetPeakValue(out var peak);
            return peak > 0.001f;
        });
    }

    private static string? GetDeviceName(EDataFlow dataFlow)
    {
        object? enumeratorObj = null;
        IMMDevice? device = null;
        object? storeObj = null;
        try
        {
            enumeratorObj = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObj;
            enumerator.GetDefaultAudioEndpoint(dataFlow, ERole.eMultimedia, out device);

            device.OpenPropertyStore(StgmRead, out storeObj);
            var store = (IPropertyStore)storeObj;

            var key = PropertyKeyDeviceFriendlyName;
            store.GetValue(ref key, out var value);
            try
            {
                return value.VarType == VtLpwstr && value.PointerValue != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.PointerValue)
                    : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (storeObj is not null) Marshal.ReleaseComObject(storeObj);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumeratorObj is not null) Marshal.ReleaseComObject(enumeratorObj);
        }
    }

    private static T? WithInterface<TInterface, T>(EDataFlow dataFlow, Func<TInterface, T> action) where T : struct =>
        WithDevice(dataFlow, device =>
        {
            object? comObj = null;
            try
            {
                var iid = typeof(TInterface).GUID;
                device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out comObj);
                return action((TInterface)comObj);
            }
            finally
            {
                if (comObj is not null) Marshal.ReleaseComObject(comObj);
            }
        });

    private static T? WithDevice<T>(EDataFlow dataFlow, Func<IMMDevice, T> action) where T : struct
    {
        object? enumeratorObj = null;
        IMMDevice? device = null;
        try
        {
            enumeratorObj = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObj;
            enumerator.GetDefaultAudioEndpoint(dataFlow, ERole.eMultimedia, out device);

            return action(device);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumeratorObj is not null) Marshal.ReleaseComObject(enumeratorObj);
        }
    }

    private const int ClsCtxAll = 23;
    private const int StgmRead = 0;
    private const ushort VtLpwstr = 31;

    private static PropertyKey PropertyKeyDeviceFriendlyName => new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
        public PropertyKey(Guid formatId, int propertyId) { FormatId = formatId; PropertyId = propertyId; }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr PointerValue;
    }

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
        int OpenPropertyStore(int stgmAccess, [MarshalAs(UnmanagedType.IUnknown)] out object ppProperties);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out int cProps);
        int GetAt(int iProp, out PropertyKey pkey);
        int GetValue(ref PropertyKey key, out PropVariant pv);
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

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float pfPeak);
    }
}
