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

    public async Task<SensorSnapshot> CollectAsync(CancellationToken ct = default) => new(
        SampleCpuPercent(),
        SampleMemoryPercent(),
        SampleBatteryPercent(),
        CrossPlatformMetrics.SampleDiskPercent(),
        CrossPlatformMetrics.SampleUptimeHours(),
        SampleActiveWindowTitle(),
        await SampleGpuPercentAsync(),
        CrossPlatformMetrics.SampleNetworkThroughputMbps());

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
            // GPU Engine instances appear/disappear as processes start/stop using the GPU,
            // so the instance list is periodically refreshed rather than read once.
            if (_gpuEngineCounters is null || DateTime.UtcNow - _gpuCountersRefreshedAt > TimeSpan.FromSeconds(10))
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
