using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace HaDesktop.Core.Interop;

/// <summary>
/// Creates and releases COM objects through source-generated interop ([GeneratedComInterface])
/// instead of the runtime's built-in COM support ([ComImport], Activator.CreateInstance on a
/// CLSID), which doesn't exist in a NativeAOT build and can't be analyzed by the trimmer.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ComActivation
{
    private const uint ClsCtxAll = 23;
    private const int CoENotInitialized = unchecked((int)0x800401F0);
    private const uint CoInitMultithreaded = 0;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Instantiates a COM class and returns it as <typeparamref name="T"/>. Throws if the class can't be created.</summary>
    public static T Create<T>(Guid clsid, Guid interfaceId) where T : class
    {
        var hr = CoCreateInstance(in clsid, IntPtr.Zero, ClsCtxAll, in interfaceId, out var pointer);
        if (hr == CoENotInitialized)
        {
            // A thread-pool thread the runtime hasn't put in an apartment yet.
            CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
            hr = CoCreateInstance(in clsid, IntPtr.Zero, ClsCtxAll, in interfaceId, out pointer);
        }

        Marshal.ThrowExceptionForHR(hr);
        return Wrap<T>(pointer);
    }

    /// <summary>
    /// Wraps an interface pointer the caller owns one reference to, taking over that reference.
    /// Always as a unique instance: only those are actually released by <see cref="Release"/> —
    /// a shared, cached wrapper ignores it and holds its COM object (and whatever handles that
    /// owns) until the finalizer gets to it.
    /// </summary>
    public static T Wrap<T>(IntPtr pointer) where T : class
    {
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>
    /// Releases a COM object now rather than whenever its wrapper happens to be finalized. Interface
    /// out-parameters need [MarshalUsing(typeof(UniqueComInterfaceMarshaller&lt;T&gt;))] for this to
    /// apply to them too.
    /// </summary>
    public static void Release(object? comObject) => (comObject as ComObject)?.FinalRelease();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint clsContext, in Guid interfaceId, out IntPtr instance);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
}
