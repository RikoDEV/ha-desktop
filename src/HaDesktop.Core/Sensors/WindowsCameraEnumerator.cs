using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HaDesktop.Core.Sensors;

/// <summary>
/// Lists connected cameras via DirectShow's classic device-enumeration API (ICreateDevEnum +
/// CLSID_VideoInputDeviceCategory) — still the standard, ceremony-free way to enumerate capture
/// devices from a plain desktop process; Media Foundation's equivalent needs an app-container/MTA
/// dance for the same result. IMoniker's full vtable is declared up through BindToStorage (the only
/// method actually called) purely to keep its slot at the right index — the placeholder methods
/// before it are never invoked, so their (deliberately unfaithful) signatures don't matter.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsCameraEnumerator
{
    /// <summary>Friendly name of the first available camera, or null if none is connected.</summary>
    public static string? GetFirstCameraName()
    {
        object? sysDevEnumObj = null;
        IEnumMoniker? enumMoniker = null;
        IMoniker? moniker = null;
        object? propBagObj = null;
        try
        {
            sysDevEnumObj = new SystemDeviceEnumComObject();
            var createDevEnum = (ICreateDevEnum)sysDevEnumObj;

            var videoCategory = VideoInputDeviceCategory;
            var hr = createDevEnum.CreateClassEnumerator(ref videoCategory, out enumMoniker, 0);
            if (hr != 0 || enumMoniker is null) return null; // S_FALSE (no devices) or failure

            if (enumMoniker.Next(1, out moniker, out var fetched) != 0 || fetched == 0 || moniker is null)
                return null;

            var bagIid = typeof(IPropertyBag).GUID;
            moniker.BindToStorage(IntPtr.Zero, IntPtr.Zero, ref bagIid, out propBagObj);
            var propBag = (IPropertyBag)propBagObj!;

            object? value = null;
            propBag.Read("FriendlyName", ref value, IntPtr.Zero);
            return value as string;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (propBagObj is not null) Marshal.ReleaseComObject(propBagObj);
            if (moniker is not null) Marshal.ReleaseComObject(moniker);
            if (enumMoniker is not null) Marshal.ReleaseComObject(enumMoniker);
            if (sysDevEnumObj is not null) Marshal.ReleaseComObject(sysDevEnumObj);
        }
    }

    private static Guid VideoInputDeviceCategory => new("860BB310-5D01-11d0-BD3B-00A0C911CE86");

    [ComImport, Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86")]
    private class SystemDeviceEnumComObject { }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        int CreateClassEnumerator(ref Guid pType, out IEnumMoniker? ppEnumMoniker, int dwFlags);
    }

    [ComImport, Guid("00000102-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumMoniker
    {
        int Next(int celt, out IMoniker? rgelt, out int pceltFetched);
        int Skip(int celt);
        int Reset();
        int Clone(out IEnumMoniker ppenum);
    }

    [ComImport, Guid("0000000f-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMoniker
    {
        int GetClassIdUnused();
        int IsDirtyUnused();
        int LoadUnused();
        int SaveUnused();
        int GetSizeMaxUnused();
        int BindToObjectUnused();
        int BindToStorage(IntPtr pbc, IntPtr pmkToLeft, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppvObj);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        int Read([MarshalAs(UnmanagedType.LPWStr)] string propName, ref object? propValue, IntPtr errorLog);
    }
}
