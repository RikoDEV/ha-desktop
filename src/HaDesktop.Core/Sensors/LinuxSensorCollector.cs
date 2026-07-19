using System.Runtime.Versioning;

namespace HaDesktop.Core.Sensors;

[SupportedOSPlatform("linux")]
public sealed class LinuxSensorCollector : ISystemSensorCollector
{
    private (long Idle, long Total)? _lastCpuSample;

    public async Task<SensorSnapshot> CollectAsync(CancellationToken ct = default) => new(
        await SampleCpuPercentAsync(ct),
        await SampleMemoryPercentAsync(ct),
        await SampleBatteryPercentAsync(ct),
        CrossPlatformMetrics.SampleDiskPercent(),
        CrossPlatformMetrics.SampleUptimeHours(),
        ActiveWindowTitle: null, // varies too much across X11/Wayland WMs to support generically yet
        GpuPercent: await SampleGpuPercentAsync(),
        NetworkMbps: CrossPlatformMetrics.SampleNetworkThroughputMbps());

    /// <summary>NVIDIA via nvidia-smi first; otherwise the amdgpu kernel driver's sysfs busy-percent file (AMD only — Intel has no equivalent).</summary>
    private static async Task<double?> SampleGpuPercentAsync()
    {
        var nvidia = await CrossPlatformMetrics.SampleNvidiaGpuPercentAsync();
        return nvidia ?? SampleAmdGpuPercent();
    }

    private static double? SampleAmdGpuPercent()
    {
        const string root = "/sys/class/drm";
        if (!Directory.Exists(root)) return null;

        try
        {
            var busyFiles = Directory.GetDirectories(root, "card*")
                .Select(dir => Path.Combine(dir, "device", "gpu_busy_percent"))
                .Where(File.Exists);

            foreach (var file in busyFiles)
            {
                var text = File.ReadAllText(file).Trim();
                if (double.TryParse(text, out var value))
                    return Math.Clamp(value, 0, 100);
            }
        }
        catch (IOException) { /* fall through to null */ }
        catch (UnauthorizedAccessException) { /* fall through to null */ }

        return null;
    }

    private async Task<double?> SampleCpuPercentAsync(CancellationToken ct)
    {
        string? line;
        try
        {
            line = (await File.ReadAllLinesAsync("/proc/stat", ct)).FirstOrDefault(l => l.StartsWith("cpu "));
        }
        catch (IOException) { return null; }
        if (line is null) return null;

        // user nice system idle iowait irq softirq steal ...
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Select(p => long.TryParse(p, out var v) ? v : 0).ToArray();
        if (parts.Length < 4) return null;

        var idle = parts[3] + (parts.Length > 4 ? parts[4] : 0); // idle + iowait
        var total = parts.Sum();

        if (_lastCpuSample is not { } last)
        {
            _lastCpuSample = (idle, total);
            return null;
        }

        var idleDelta = idle - last.Idle;
        var totalDelta = total - last.Total;
        _lastCpuSample = (idle, total);

        if (totalDelta <= 0) return 0;
        return Math.Clamp((totalDelta - idleDelta) * 100.0 / totalDelta, 0, 100);
    }

    private static async Task<double?> SampleMemoryPercentAsync(CancellationToken ct)
    {
        string[] lines;
        try { lines = await File.ReadAllLinesAsync("/proc/meminfo", ct); }
        catch (IOException) { return null; }

        long total = 0, available = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("MemTotal:")) total = ParseKb(line);
            else if (line.StartsWith("MemAvailable:")) available = ParseKb(line);
        }

        if (total <= 0) return null;
        return Math.Clamp((total - available) * 100.0 / total, 0, 100);
    }

    private static long ParseKb(string line)
    {
        var digits = new string(line.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var value) ? value : 0;
    }

    private static async Task<double?> SampleBatteryPercentAsync(CancellationToken ct)
    {
        const string root = "/sys/class/power_supply";
        if (!Directory.Exists(root)) return null;

        var batteryDir = Directory.GetDirectories(root).FirstOrDefault(d => Path.GetFileName(d).StartsWith("BAT"));
        if (batteryDir is null) return null;

        var capacityFile = Path.Combine(batteryDir, "capacity");
        if (!File.Exists(capacityFile)) return null;

        try
        {
            var text = await File.ReadAllTextAsync(capacityFile, ct);
            return double.TryParse(text.Trim(), out var value) ? value : null;
        }
        catch (IOException) { return null; }
    }
}
