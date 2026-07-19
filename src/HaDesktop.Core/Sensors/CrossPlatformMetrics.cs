using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace HaDesktop.Core.Sensors;

/// <summary>Metrics with a genuinely cross-platform .NET API — no per-OS collector needed.</summary>
internal static class CrossPlatformMetrics
{
    private static (long Bytes, DateTime Timestamp)? _lastNetworkSample;

    public static double? SampleDiskPercent()
    {
        try
        {
            var rootPath = OperatingSystem.IsWindows()
                ? Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\"
                : "/";

            var drive = new DriveInfo(rootPath);
            if (!drive.IsReady || drive.TotalSize <= 0) return null;

            var used = drive.TotalSize - drive.AvailableFreeSpace;
            return Math.Clamp(used * 100.0 / drive.TotalSize, 0, 100);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static double SampleUptimeHours() => Environment.TickCount64 / 1000.0 / 3600.0;

    /// <summary>
    /// Combined send+receive throughput of the busiest active, non-loopback
    /// interface, in Mbit/s. Computed from the delta since the previous call
    /// (like the CPU% samplers), so the first call always returns null.
    /// </summary>
    public static double? SampleNetworkThroughputMbps()
    {
        try
        {
            var iface = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => (Interface: n, Stats: n.GetIPv4Statistics()))
                .OrderByDescending(x => x.Stats.BytesSent + x.Stats.BytesReceived)
                .FirstOrDefault();

            if (iface.Interface is null) return null;

            var totalBytes = iface.Stats.BytesSent + iface.Stats.BytesReceived;
            var now = DateTime.UtcNow;

            if (_lastNetworkSample is not { } last)
            {
                _lastNetworkSample = (totalBytes, now);
                return null;
            }

            var elapsedSeconds = (now - last.Timestamp).TotalSeconds;
            var deltaBytes = totalBytes - last.Bytes;
            _lastNetworkSample = (totalBytes, now);

            if (elapsedSeconds <= 0 || deltaBytes < 0) return null; // counter reset (interface reconnect, sleep/resume, etc.)

            return Math.Round(deltaBytes * 8.0 / 1_000_000.0 / elapsedSeconds, 2);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort NVIDIA GPU utilization via `nvidia-smi` — vendor-specific,
    /// but the tool itself is available on both Windows and Linux wherever
    /// NVIDIA drivers are installed, so callers on either OS try this first.
    /// AMD GPU sampling is OS-specific (perf counters / sysfs) and lives in
    /// each platform's own SensorCollector instead.
    /// </summary>
    public static async Task<double?> SampleNvidiaGpuPercentAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--query-gpu=utilization.gpu");
            psi.ArgumentList.Add("--format=csv,noheader,nounits");

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return firstLine is not null && double.TryParse(firstLine.Trim(), out var value) ? value : null;
        }
        catch (Win32Exception)
        {
            return null; // nvidia-smi not installed / no NVIDIA GPU
        }
    }
}
