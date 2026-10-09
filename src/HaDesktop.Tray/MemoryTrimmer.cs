using System;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;

namespace HaDesktop.Tray;

/// <summary>
/// Hands memory back to the OS after the app's rare bursts of allocation — starting up and loading
/// every entity's state once, or building and then closing the Settings window. A tray app spends
/// nearly all its time idle, allocating too little to make the GC collect on its own, so without
/// this the high-water mark of each burst stays committed for hours.
/// </summary>
internal static class MemoryTrimmer
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(10);

    private static int _pending;

    /// <summary>Schedules one compacting collection a few seconds from now, once whatever just finished has gone quiet. Calls while one is already scheduled are ignored.</summary>
    public static void TrimSoon()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(SettleDelay);
            Interlocked.Exchange(ref _pending, 0);

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        });
    }
}
