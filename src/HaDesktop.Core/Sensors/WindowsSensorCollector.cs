using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HaDesktop.Core.Sensors;

[SupportedOSPlatform("windows")]
public sealed class WindowsSensorCollector : ISystemSensorCollector
{
    private (long Idle, long Kernel, long User)? _lastCpuSample;
    private List<PerformanceCounter>? _gpuEngineCounters;
    private DateTime _gpuCountersRefreshedAt = DateTime.MinValue;
    private PerformanceCounter? _diskIdleTimeCounter;
    private bool _diskCounterPrimed;
    private PerformanceCounter? _diskReadBytesCounter;
    private PerformanceCounter? _diskWriteBytesCounter;
    private bool _diskThroughputCounterPrimed;

    public async Task<SensorSnapshot> CollectAsync(CancellationToken ct = default)
    {
        var (volumePercent, isMuted) = WindowsAudioEndpoint.GetState();
        var (ssid, bssid) = await WindowsWifiInfo.GetWifiInfoAsync(ct);
        return new(
            SampleCpuPercent(),
            SampleMemoryPercent(),
            SampleBatteryPercent(),
            SampleDiskActivePercent(),
            CrossPlatformMetrics.SampleUptimeHours(),
            SampleActiveWindowTitle(),
            await SampleGpuPercentAsync(),
            CrossPlatformMetrics.SampleNetworkThroughputMbps(),
            CrossPlatformMetrics.SampleDiskPercent(),
            SampleDiskThroughputMbps(),
            SampleIsSessionLocked(),
            volumePercent,
            isMuted,
            WindowsAudioEndpoint.GetOutputDeviceName(),
            WindowsAudioEndpoint.GetInputDeviceName(),
            WindowsAudioEndpoint.IsOutputActive(),
            WindowsPrivacyConsentStore.IsMicrophoneInUse(),
            WindowsCameraEnumerator.GetFirstCameraName(),
            WindowsPrivacyConsentStore.IsCameraInUse(),
            ssid,
            bssid,
            WindowsWifiInfo.GetConnectionType(ssid),
            WindowsDisplayInfo.GetDisplayCount(),
            WindowsDisplayInfo.GetPrimaryDisplayDescription());
    }

    /// <summary>Combined read+write throughput of the system drive, in Mbit/s — same "Bytes/sec" counter family as the GPU/disk-activity ones, kept alive across polls for the same reason.</summary>
    private double? SampleDiskThroughputMbps()
    {
        try
        {
            _diskReadBytesCounter ??= new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", readOnly: true);
            _diskWriteBytesCounter ??= new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", readOnly: true);

            var readBytesPerSec = _diskReadBytesCounter.NextValue();
            var writeBytesPerSec = _diskWriteBytesCounter.NextValue();

            if (!_diskThroughputCounterPrimed)
            {
                _diskThroughputCounterPrimed = true;
                return null; // priming sample, not a real reading
            }

            return Math.Round((readBytesPerSec + writeBytesPerSec) * 8.0 / 1_000_000.0, 2);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The lock screen runs on a separate desktop that the interactive session can't switch to
    /// while locked, so <c>OpenInputDesktop</c> failing is the standard way to detect a locked
    /// workstation without registering for WTS session-change notifications.
    /// </summary>
    private static bool? SampleIsSessionLocked()
    {
        try
        {
            var desktop = OpenInputDesktop(0, false, DesktopSwitchDesktop);
            if (desktop == IntPtr.Zero) return true;
            CloseDesktop(desktop);
            return false;
        }
        catch
        {
            return null;
        }
    }

    private const uint DesktopSwitchDesktop = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    /// <summary>
    /// Matches Task Manager's "Disk" percentage — how busy the disk's I/O is right now — not how
    /// full it is. <see cref="CrossPlatformMetrics.SampleDiskPercent"/> (used capacity / total
    /// capacity) reported a number that barely moves and doesn't correspond to what "Disk Usage"
    /// looks like anywhere else in Windows, which is what a user comparing against Task Manager
    /// actually expects. "% Idle Time" is the standard PhysicalDisk counter for this (Resource
    /// Monitor derives its own Disk Active Time the same way) — active% is just its complement.
    /// The counter instance is kept alive for the process lifetime, not recreated per call, for the
    /// same reason the GPU counters are: a freshly constructed counter's first NextValue() has no
    /// prior sample to diff against and would otherwise look like 100% (fully idle -> 0% active)
    /// forever if reset every poll.
    /// </summary>
    private double? SampleDiskActivePercent()
    {
        try
        {
            _diskIdleTimeCounter ??= new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", readOnly: true);
            var idlePercent = _diskIdleTimeCounter.NextValue();

            if (!_diskCounterPrimed)
            {
                _diskCounterPrimed = true;
                return null; // this first read is the priming sample, not a real one
            }

            return Math.Clamp(100 - idlePercent, 0, 100);
        }
        catch (InvalidOperationException)
        {
            return null; // "PhysicalDisk" category/instance not present
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>NVIDIA via nvidia-smi first (works identically across OSes); otherwise falls back to the
    /// "GPU Engine" performance counter category, which Windows populates for any vendor's driver (AMD, Intel).</summary>
    private async Task<double?> SampleGpuPercentAsync()
    {
        var nvidia = await CrossPlatformMetrics.SampleNvidiaGpuPercentAsync();
        return nvidia ?? SampleGpuPercentViaPerformanceCounters();
    }

    private double? SampleGpuPercentViaPerformanceCounters()
    {
        try
        {
            // GPU Engine instances appear/disappear as processes start/stop using the GPU, so the
            // instance list is periodically refreshed — but much less often than callers actually
            // poll (AppSettings' sensor push runs every 30s). A freshly (re)created counter's first
            // NextValue() has no prior sample to diff against and returns null by design, so a
            // refresh window shorter than the polling interval — this used to be 10s — meant every
            // single poll recreated the counters and got null forever, never actually reporting GPU
            // usage. Keeping the same counter instances alive across many polls, and only refreshing
            // the list occasionally to catch new/removed GPU processes, is what lets NextValue() see
            // a real elapsed-time delta on the (very common) second-and-later call.
            if (_gpuEngineCounters is null || DateTime.UtcNow - _gpuCountersRefreshedAt > TimeSpan.FromMinutes(5))
            {
                foreach (var counter in _gpuEngineCounters ?? Enumerable.Empty<PerformanceCounter>())
                    counter.Dispose();

                // Every engine instance is tracked, not just "engtype_3D" — some AMD driver
                // builds report the bulk of GPU activity under Compute/VideoDecode/Copy instead
                // of 3D depending on workload, so a 3D-only filter could sit at 0% even under load.
                var category = new PerformanceCounterCategory("GPU Engine");
                _gpuEngineCounters = category.GetInstanceNames()
                    .Select(name => new PerformanceCounter("GPU Engine", "Utilization Percentage", name, readOnly: true))
                    .ToList();
                _gpuCountersRefreshedAt = DateTime.UtcNow;

                // A freshly created counter's first NextValue() is always 0 (no prior sample to diff against).
                foreach (var counter in _gpuEngineCounters)
                    counter.NextValue();
                return null;
            }

            if (_gpuEngineCounters.Count == 0) return null;

            // Task Manager's single "GPU %" figure is the busiest engine type at a given
            // moment (3D, Compute, Video Decode/Encode, Copy) — summing every engine type
            // together would double-count a workload that touches several of them at once.
            var byEngineType = _gpuEngineCounters
                .GroupBy(c => GetEngineType(c.InstanceName))
                .Select(g => g.Sum(c => c.NextValue()));

            return Math.Clamp(byEngineType.DefaultIfEmpty(0).Max(), 0, 100);
        }
        catch (InvalidOperationException)
        {
            return null; // "GPU Engine" category not present (no driver exposing it)
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GetEngineType(string instanceName)
    {
        var index = instanceName.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? instanceName[index..] : instanceName;
    }

    private static string? SampleActiveWindowTitle()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return null;

        var length = GetWindowTextLength(handle);
        if (length <= 0) return null;

        var buffer = new System.Text.StringBuilder(length + 1);
        return GetWindowText(handle, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    private double? SampleCpuPercent()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            return null;

        var idle = ToInt64(idleFt);
        var kernel = ToInt64(kernelFt); // kernel time includes idle time on Windows
        var user = ToInt64(userFt);

        if (_lastCpuSample is not { } last)
        {
            _lastCpuSample = (idle, kernel, user);
            return null;
        }

        var idleDelta = idle - last.Idle;
        var totalDelta = (kernel - last.Kernel) + (user - last.User);
        _lastCpuSample = (idle, kernel, user);

        if (totalDelta <= 0) return 0;
        return Math.Clamp((totalDelta - idleDelta) * 100.0 / totalDelta, 0, 100);
    }

    private static double? SampleMemoryPercent()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad : null;
    }

    private static double? SampleBatteryPercent()
    {
        if (!GetSystemPowerStatus(out var status) || status.BatteryLifePercent == 255)
            return null; // 255 = "unknown" (desktops with no battery report this)
        return status.BatteryLifePercent;
    }

    private static long ToInt64(FILETIME ft) => ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}
