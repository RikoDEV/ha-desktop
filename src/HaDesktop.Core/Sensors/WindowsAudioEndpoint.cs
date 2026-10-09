using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using HaDesktop.Core.Diagnostics;
using HaDesktop.Core.Interop;

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
internal static partial class WindowsAudioEndpoint
{
    public static (double? VolumePercent, bool? IsMuted) GetState()
    {
        return WithInterface<IAudioEndpointVolume, (double?, bool?)>(EDataFlow.eRender, AudioEndpointVolumeId, endpointVolume =>
        {
            Marshal.ThrowExceptionForHR(endpointVolume.GetMasterVolumeLevelScalar(out var level));
            Marshal.ThrowExceptionForHR(endpointVolume.GetMute(out var muted));
            return (Math.Round(level * 100.0, 0), muted != 0);
        }) ?? (null, null);
    }

    public static bool SetMute(bool muted)
    {
        var eventContext = Guid.Empty;
        return WithInterface<IAudioEndpointVolume, bool>(EDataFlow.eRender, AudioEndpointVolumeId, endpointVolume =>
            endpointVolume.SetMute(muted ? 1 : 0, in eventContext) >= 0) ?? false;
    }

    public static bool SetVolumePercent(double percent)
    {
        var level = (float)(Math.Clamp(percent, 0, 100) / 100.0);
        var eventContext = Guid.Empty;
        return WithInterface<IAudioEndpointVolume, bool>(EDataFlow.eRender, AudioEndpointVolumeId, endpointVolume =>
            endpointVolume.SetMasterVolumeLevelScalar(level, in eventContext) >= 0) ?? false;
    }

    /// <summary>Friendly name of the default playback device (e.g. "Speakers (Realtek Audio)"), or null if there isn't one.</summary>
    public static string? GetOutputDeviceName() => GetDeviceName(EDataFlow.eRender);

    /// <summary>Friendly name of the default recording device (e.g. "Microphone (Realtek Audio)"), or null if there isn't one.</summary>
    public static string? GetInputDeviceName() => GetDeviceName(EDataFlow.eCapture);

    /// <summary>True if the default playback device currently has an active (non-silent) signal — i.e. something is actually playing sound right now.</summary>
    public static bool? IsOutputActive()
    {
        return WithInterface<IAudioMeterInformation, bool>(EDataFlow.eRender, AudioMeterInformationId, meter =>
        {
            Marshal.ThrowExceptionForHR(meter.GetPeakValue(out var peak));
            return peak > 0.001f;
        });
    }

    private static string? GetDeviceName(EDataFlow dataFlow)
    {
        return WithDevice<string>(dataFlow, device =>
        {
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StgmRead, out var store));
            try
            {
                var key = PropertyKeyDeviceFriendlyName;
                Marshal.ThrowExceptionForHR(store.GetValue(in key, out var value));
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
            finally
            {
                ComActivation.Release(store);
            }
        });
    }

    private static T? WithInterface<TInterface, T>(EDataFlow dataFlow, Guid interfaceId, Func<TInterface, T> action)
        where TInterface : class
        where T : struct
    {
        T? result = null;
        WithDevice<object>(dataFlow, device =>
        {
            Marshal.ThrowExceptionForHR(device.Activate(in interfaceId, ClsCtxAll, IntPtr.Zero, out var pointer));
            var activated = ComActivation.Wrap<TInterface>(pointer);
            try
            {
                result = action(activated);
            }
            finally
            {
                ComActivation.Release(activated);
            }

            return null;
        });
        return result;
    }

    /// <summary>Runs <paramref name="action"/> against the default endpoint for a direction; null if there isn't one or anything along the way fails.</summary>
    private static T? WithDevice<T>(EDataFlow dataFlow, Func<IMMDevice, T?> action) where T : class
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = ComActivation.Create<IMMDeviceEnumerator>(MMDeviceEnumeratorClsid, MMDeviceEnumeratorId);
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(dataFlow, ERole.eMultimedia, out device));
            return action(device);
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            return null;
        }
        finally
        {
            ComActivation.Release(device);
            ComActivation.Release(enumerator);
        }
    }

    private const int ClsCtxAll = 23;
    private const int StgmRead = 0;
    private const ushort VtLpwstr = 31;

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MMDeviceEnumeratorId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid AudioEndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid AudioMeterInformationId = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    private static PropertyKey PropertyKeyDeviceFriendlyName => new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
        public PropertyKey(Guid formatId, int propertyId) { FormatId = formatId; PropertyId = propertyId; }
    }

    // A PROPVARIANT is 24 bytes on 64-bit (16 on 32-bit); only the type tag and a pointer-sized payload are read.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr PointerValue;
    }

    internal enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    internal enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    // Every method keeps its real HRESULT return ([PreserveSig]); methods this app never calls are
    // declared only to keep the ones it does call at the right vtable slot.

    [GeneratedComInterface, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal partial interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDevice>))] out IMMDevice ppEndpoint);
    }

    [GeneratedComInterface, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    internal partial interface IMMDevice
    {
        [PreserveSig] int Activate(in Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IPropertyStore>))] out IPropertyStore ppProperties);
    }

    [GeneratedComInterface, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    internal partial interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int cProps);
        [PreserveSig] int GetAt(int iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(in PropertyKey key, out PropVariant pv);
    }

    [GeneratedComInterface, Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    internal partial interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out int channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float level, in Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, in Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, in Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, in Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute(int isMuted, in Guid eventContext);
        [PreserveSig] int GetMute(out int isMuted);
    }

    [GeneratedComInterface, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    internal partial interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float pfPeak);
    }
}
