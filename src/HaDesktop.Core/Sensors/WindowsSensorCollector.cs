using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HaDesktop.Core.Diagnostics;
using HaDesktop.Core.Storage;

namespace HaDesktop.Core.Sensors;

[SupportedOSPlatform("windows")]
public sealed class WindowsSensorCollector : ISystemSensorCollector
{
    private static readonly TimeSpan WifiCacheLifetime = TimeSpan.FromMinutes(2);

    // The settings window probes GPU availability through this same instance while the 30s sensor
    // timer may be mid-collection; the delta-based samplers and PDH queries below aren't reentrant.
    private readonly SemaphoreSlim _collectLock = new(1, 1);

    private (long Idle, long Kernel, long User)? _lastCpuSample;
    private PdhQuery? _gpuQuery;
    private bool _gpuQueryUnavailable;
    private readonly Dictionary<string, double> _gpuLoadByEngineType = new();
    private readonly PdhQuery.InstanceReader _addGpuEngineLoad;

    public WindowsSensorCollector() => _addGpuEngineLoad = AddGpuEngineLoad;
    private PdhQuery? _diskQuery;
    private bool _diskQueryUnavailable;
    private (string? Ssid, string? Bssid, string? ConnectionType, DateTime FetchedAt)? _networkIdentity;

    public async Task<SensorSnapshot> CollectAsync(SensorPreferences prefs, CancellationToken ct = default)
    {
        await _collectLock.WaitAsync(ct);
        try
        {
            var (volumePercent, isMuted) = prefs.ShareVolume ? WindowsAudioEndpoint.GetState() : default;
            var needsWifi = prefs.ShareSsid || prefs.ShareBssid || prefs.ShareConnectionType;
            var (ssid, bssid, connectionType) = needsWifi ? await GetNetworkIdentityAsync(ct) : default;
            var diskSampled = (prefs.ShareDisk || prefs.ShareDiskThroughput) && CollectDiskCounters();

            return new(
                prefs.ShareCpu ? SampleCpuPercent() : null,
                prefs.ShareMemory ? SampleMemoryPercent() : null,
                prefs.ShareBattery ? SampleBatteryPercent() : null,
                prefs.ShareDisk && diskSampled ? DiskActivePercent() : null,
                prefs.ShareUptime ? CrossPlatformMetrics.SampleUptimeHours() : null,
                prefs.ShareActiveWindow ? SampleActiveWindowTitle() : null,
                prefs.ShareGpu ? await SampleGpuPercentAsync() : null,
                prefs.ShareNetwork ? CrossPlatformMetrics.SampleNetworkThroughputMbps() : null,
                prefs.ShareStorage ? CrossPlatformMetrics.SampleDiskPercent() : null,
                prefs.ShareDiskThroughput && diskSampled ? DiskThroughputMbps() : null,
                prefs.ShareSessionLock ? SampleIsSessionLocked() : null,
                volumePercent,
                isMuted,
                prefs.ShareActiveAudioOutput ? WindowsAudioEndpoint.GetOutputDeviceName() : null,
                prefs.ShareActiveAudioInput ? WindowsAudioEndpoint.GetInputDeviceName() : null,
                prefs.ShareAudioOutputInUse ? WindowsAudioEndpoint.IsOutputActive() : null,
                prefs.ShareAudioInputInUse ? WindowsPrivacyConsentStore.IsMicrophoneInUse() : null,
                prefs.ShareActiveCamera ? WindowsCameraEnumerator.GetFirstCameraName() : null,
                prefs.ShareCameraInUse ? WindowsPrivacyConsentStore.IsCameraInUse() : null,
                ssid,
                bssid,
                prefs.ShareConnectionType ? connectionType : null,
                prefs.ShareDisplayCount ? WindowsDisplayInfo.GetDisplayCount() : null,
                prefs.SharePrimaryDisplay ? WindowsDisplayInfo.GetPrimaryDisplayDescription() : null);
        }
        finally
        {
            _collectLock.Release();
        }
    }

    /// <summary>Which network this machine is on changes rarely, and finding out means spawning netsh and listing every adapter — so the answer is reused for a couple of minutes rather than re-derived on every 30s poll.</summary>
    private async Task<(string? Ssid, string? Bssid, string? ConnectionType)> GetNetworkIdentityAsync(CancellationToken ct)
    {
        if (_networkIdentity is { } cached && DateTime.UtcNow - cached.FetchedAt < WifiCacheLifetime)
            return (cached.Ssid, cached.Bssid, cached.ConnectionType);

        var (ssid, bssid) = await WindowsWifiInfo.GetWifiInfoAsync(ct);
        var connectionType = WindowsWifiInfo.GetConnectionType(ssid);
        _networkIdentity = (ssid, bssid, connectionType, DateTime.UtcNow);
        return (ssid, bssid, connectionType);
    }

    /// <summary>
    /// Samples the system drive's activity and throughput counters together. False while there's
    /// nothing to read yet: the counters are rates, so the first sample after the query is opened
    /// only primes them. The query stays open for the process lifetime for that same reason.
    /// </summary>
    private bool CollectDiskCounters()
    {
        if (_diskQueryUnavailable) return false;

        _diskQuery ??= PdhQuery.TryOpen(
            @"\PhysicalDisk(_Total)\% Idle Time",
            @"\PhysicalDisk(_Total)\Disk Read Bytes/sec",
            @"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        if (_diskQuery is null)
        {
            _diskQueryUnavailable = true; // "PhysicalDisk" counters not present on this machine
            return false;
        }

        return _diskQuery.Collect();
    }

    /// <summary>
    /// Matches Task Manager's "Disk" percentage — how busy the disk's I/O is right now — not how
    /// full it is. <see cref="CrossPlatformMetrics.SampleDiskPercent"/> (used capacity / total
    /// capacity) reported a number that barely moves and doesn't correspond to what "Disk Usage"
    /// looks like anywhere else in Windows, which is what a user comparing against Task Manager
    /// actually expects. "% Idle Time" is the standard PhysicalDisk counter for this (Resource
    /// Monitor derives its own Disk Active Time the same way) — active% is just its complement.
    /// </summary>
    private double? DiskActivePercent() =>
        _diskQuery!.GetValue(0) is { } idlePercent ? Math.Clamp(100 - idlePercent, 0, 100) : null;

    /// <summary>Combined read+write throughput of the system drive, in Mbit/s.</summary>
    private double? DiskThroughputMbps() =>
        _diskQuery!.GetValue(1) is { } readBytesPerSec && _diskQuery.GetValue(2) is { } writeBytesPerSec
            ? Math.Round((readBytesPerSec + writeBytesPerSec) * 8.0 / 1_000_000.0, 2)
            : null;

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
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            return null;
        }
    }

    private const uint DesktopSwitchDesktop = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    /// <summary>NVIDIA via nvidia-smi first (works identically across OSes); otherwise falls back to the
    /// "GPU Engine" performance counter category, which Windows populates for any vendor's driver (AMD, Intel).</summary>
    private async Task<double?> SampleGpuPercentAsync()
    {
        var nvidia = await CrossPlatformMetrics.SampleNvidiaGpuPercentAsync();
        return nvidia ?? SampleGpuPercentViaPerformanceCounters();
    }

    private double? SampleGpuPercentViaPerformanceCounters()
    {
        if (_gpuQueryUnavailable) return null;

        // One wildcard counter covers every engine of every GPU process, and PDH picks up engines
        // that appear or go away on its own at each collection. Every engine instance is tracked,
        // not just "engtype_3D" — some AMD driver builds report the bulk of GPU activity under
        // Compute/VideoDecode/Copy instead of 3D depending on workload, so a 3D-only filter could
        // sit at 0% even under load.
        _gpuQuery ??= PdhQuery.TryOpen(@"\GPU Engine(*)\Utilization Percentage");
        if (_gpuQuery is null)
        {
            _gpuQueryUnavailable = true; // "GPU Engine" category not present (no driver exposing it)
            return null;
        }

        if (!_gpuQuery.Collect()) return null; // first sample only primes the rate counters

        // Task Manager's single "GPU %" figure is the busiest engine type at a given
        // moment (3D, Compute, Video Decode/Encode, Copy) — summing every engine type
        // together would double-count a workload that touches several of them at once.
        _gpuLoadByEngineType.Clear();
        if (!_gpuQuery.ReadInstances(0, _addGpuEngineLoad)) return null;

        var busiest = 0.0;
        foreach (var load in _gpuLoadByEngineType.Values) busiest = Math.Max(busiest, load);
        return Math.Clamp(busiest, 0, 100);
    }

    /// <summary>Adds one engine instance's load to its engine type's total. Looked up by span, so only the handful of distinct engine types become strings — not each of the hundreds of instance names.</summary>
    private void AddGpuEngineLoad(ReadOnlySpan<char> instanceName, double value)
    {
        var index = instanceName.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        var engineType = index >= 0 ? instanceName[index..] : instanceName;

        var totals = _gpuLoadByEngineType.GetAlternateLookup<ReadOnlySpan<char>>();
        totals[engineType] = (totals.TryGetValue(engineType, out var total) ? total : 0) + value;
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
