using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HaDesktop.Core.Sensors;

/// <summary>
/// A Windows performance-counter query through PDH directly. One query handle covers any number
/// of counters — including a wildcard path such as "\GPU Engine(*)\Utilization Percentage", which
/// PDH re-expands on every collection — where System.Diagnostics.PerformanceCounter needed one
/// managed object and one set of handles per counter instance (hundreds, for the GPU engines of a
/// busy desktop) and loaded the registry's whole counter-name table into the process to resolve them.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PdhQuery : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCstatusValidData = 0;
    private const uint PdhCstatusNewData = 1;

    // PDH_FMT_COUNTERVALUE_ITEM_W: a name pointer, then a PDH_FMT_COUNTERVALUE (status + 8-byte
    // union) aligned to 8 — the same offsets on both 32- and 64-bit.
    private const int ItemStatusOffset = 8;
    private const int ItemValueOffset = 16;
    private const int ItemSize = 24;

    private IntPtr _query;
    private readonly IntPtr[] _counters;
    private bool _hasPreviousSample;

    private PdhQuery(IntPtr query, IntPtr[] counters)
    {
        _query = query;
        _counters = counters;
    }

    /// <summary>
    /// Opens a query over the given counter paths, written with their English names (PDH resolves
    /// those on any display language). Null if PDH or any one of the counters isn't available.
    /// </summary>
    public static PdhQuery? TryOpen(params string[] englishCounterPaths)
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0) return null;

            var counters = new IntPtr[englishCounterPaths.Length];
            for (var i = 0; i < englishCounterPaths.Length; i++)
            {
                if (PdhAddEnglishCounterW(query, englishCounterPaths[i], IntPtr.Zero, out counters[i]) != 0)
                {
                    PdhCloseQuery(query);
                    return null;
                }
            }

            return new PdhQuery(query, counters);
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    /// <summary>
    /// Takes a new sample. These are all rate counters, computed from the difference between two
    /// samples, so this returns false for the very first one — there's nothing to read yet.
    /// </summary>
    public bool Collect()
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return false;

        var ready = _hasPreviousSample;
        _hasPreviousSample = true;
        return ready;
    }

    /// <summary>The value of a single-instance counter as of the last <see cref="Collect"/>.</summary>
    public double? GetValue(int counterIndex)
    {
        if (PdhGetFormattedCounterValue(_counters[counterIndex], PdhFmtDouble, out _, out var value) != 0) return null;
        return value.Status is PdhCstatusValidData or PdhCstatusNewData ? value.DoubleValue : null;
    }

    /// <summary>
    /// Every instance of a wildcard counter as of the last <see cref="Collect"/>, passed to
    /// <paramref name="onInstance"/> as (instance name, value). Instances with no usable value yet
    /// (they appeared since the previous sample) are left out. False if nothing could be read.
    /// </summary>
    public bool ReadInstances(int counterIndex, Action<string, double> onInstance)
    {
        uint bufferSize = 0;
        var status = PdhGetFormattedCounterArrayW(_counters[counterIndex], PdhFmtDouble, ref bufferSize, out _, IntPtr.Zero);
        if (status != PdhMoreData || bufferSize == 0) return false;

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            if (PdhGetFormattedCounterArrayW(_counters[counterIndex], PdhFmtDouble, ref bufferSize, out var itemCount, buffer) != 0)
                return false;

            for (var i = 0; i < itemCount; i++)
            {
                var item = buffer + i * ItemSize;
                var itemStatus = (uint)Marshal.ReadInt32(item, ItemStatusOffset);
                if (itemStatus is not (PdhCstatusValidData or PdhCstatusNewData)) continue;

                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                if (name is null) continue;

                onInstance(name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, ItemValueOffset)));
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;
        PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint Status;
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
