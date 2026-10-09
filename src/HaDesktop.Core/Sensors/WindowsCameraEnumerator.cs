using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using HaDesktop.Core.Diagnostics;
using HaDesktop.Core.Interop;

namespace HaDesktop.Core.Sensors;

/// <summary>
/// Lists connected cameras via DirectShow's classic device-enumeration API (ICreateDevEnum +
/// CLSID_VideoInputDeviceCategory) — still the standard, ceremony-free way to enumerate capture
/// devices from a plain desktop process; Media Foundation's equivalent needs an app-container/MTA
/// dance for the same result. IMoniker's vtable is declared up through BindToStorage (the only
/// method actually called) purely to keep its slot at the right index — the placeholder methods
/// before it are never invoked, so their (deliberately unfaithful) signatures don't matter.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsCameraEnumerator
{
    private const ushort VtBstr = 8;

    private static readonly Guid SystemDeviceEnumClsid = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    private static readonly Guid CreateDevEnumId = new("29840822-5B84-11D0-BD3B-00A0C911CE86");
    private static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    private static readonly Guid PropertyBagId = new("55272A00-42CB-11CE-8135-00AA004BB851");

    /// <summary>Friendly name of the first available camera, or null if none is connected.</summary>
    public static string? GetFirstCameraName()
    {
        ICreateDevEnum? createDevEnum = null;
        IEnumMoniker? enumMoniker = null;
        IMoniker? moniker = null;
        IPropertyBag? propertyBag = null;
        try
        {
            createDevEnum = ComActivation.Create<ICreateDevEnum>(SystemDeviceEnumClsid, CreateDevEnumId);

            var hr = createDevEnum.CreateClassEnumerator(in VideoInputDeviceCategory, out enumMoniker, 0);
            if (hr != 0 || enumMoniker is null) return null; // S_FALSE (no devices) or failure

            if (enumMoniker.Next(1, out moniker, out var fetched) != 0 || fetched == 0 || moniker is null)
                return null;

            if (moniker.BindToStorage(IntPtr.Zero, IntPtr.Zero, in PropertyBagId, out var bagPointer) != 0 || bagPointer == IntPtr.Zero)
                return null;
            propertyBag = ComActivation.Wrap<IPropertyBag>(bagPointer);

            var value = default(Variant);
            if (propertyBag.Read("FriendlyName", ref value, IntPtr.Zero) != 0) return null;
            try
            {
                return value.VarType == VtBstr && value.PointerValue != IntPtr.Zero ? Marshal.PtrToStringBSTR(value.PointerValue) : null;
            }
            finally
            {
                VariantClear(ref value);
            }
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            return null;
        }
        finally
        {
            ComActivation.Release(propertyBag);
            ComActivation.Release(moniker);
            ComActivation.Release(enumMoniker);
            ComActivation.Release(createDevEnum);
        }
    }

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(ref Variant variant);

    // A VARIANT is 24 bytes on 64-bit (16 on 32-bit); only the type tag and a pointer-sized payload are read.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct Variant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr PointerValue;
    }

    [GeneratedComInterface, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
    internal partial interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator(in Guid pType, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IEnumMoniker>))] out IEnumMoniker? ppEnumMoniker, int dwFlags);
    }

    [GeneratedComInterface, Guid("00000102-0000-0000-C000-000000000046")]
    internal partial interface IEnumMoniker
    {
        [PreserveSig] int Next(int celt, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMoniker>))] out IMoniker? rgelt, out int pceltFetched);
        [PreserveSig] int Skip(int celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IntPtr ppenum);
    }

    [GeneratedComInterface, Guid("0000000f-0000-0000-C000-000000000046")]
    internal partial interface IMoniker
    {
        [PreserveSig] int GetClassIdUnused();
        [PreserveSig] int IsDirtyUnused();
        [PreserveSig] int LoadUnused();
        [PreserveSig] int SaveUnused();
        [PreserveSig] int GetSizeMaxUnused();
        [PreserveSig] int BindToObjectUnused();
        [PreserveSig] int BindToStorage(IntPtr pbc, IntPtr pmkToLeft, in Guid riid, out IntPtr ppvObj);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
    internal partial interface IPropertyBag
    {
        [PreserveSig] int Read(string propName, ref Variant propValue, IntPtr errorLog);
    }
}
