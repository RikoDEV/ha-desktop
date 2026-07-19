using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace HaDesktop.Core.Sensors;

[SupportedOSPlatform("macos")]
public sealed partial class MacSensorCollector : ISystemSensorCollector
{
    public async Task<SensorSnapshot> CollectAsync(CancellationToken ct = default) => new(
        await SampleCpuPercentAsync(ct),
        await SampleMemoryPercentAsync(ct),
        await SampleBatteryPercentAsync(ct),
        CrossPlatformMetrics.SampleDiskPercent(),
        CrossPlatformMetrics.SampleUptimeHours(),
        ActiveWindowTitle: null, // needs Accessibility permission + AppleScript/NSWorkspace — not wired up yet
        GpuPercent: await CrossPlatformMetrics.SampleNvidiaGpuPercentAsync(), // effectively always null on Macs (no NVIDIA GPUs, and no AMD sysfs/perf-counter equivalent on macOS)
        NetworkMbps: CrossPlatformMetrics.SampleNetworkThroughputMbps());

    private static async Task<double?> SampleCpuPercentAsync(CancellationToken ct)
    {
        // `top -l 1` blocks for one sampling interval and reports usage since the
        // previous system-wide sample itself, so unlike the Windows/Linux
        // collectors this doesn't need state carried between calls.
        var output = await RunAsync("top", new[] { "-l", "1", "-n", "0" }, ct);
        var match = CpuIdleRegex().Match(output);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var idle))
            return null;
        return Math.Clamp(100 - idle, 0, 100);
    }

    private static async Task<double?> SampleMemoryPercentAsync(CancellationToken ct)
    {
        if (!long.TryParse((await RunAsync("sysctl", new[] { "-n", "hw.pagesize" }, ct)).Trim(), out var pageSize))
            pageSize = 4096;
        if (!long.TryParse((await RunAsync("sysctl", new[] { "-n", "hw.memsize" }, ct)).Trim(), out var totalBytes) || totalBytes <= 0)
            return null;

        var vmStat = await RunAsync("vm_stat", Array.Empty<string>(), ct);
        long free = 0, inactive = 0;
        foreach (var line in vmStat.Split('\n'))
        {
            if (line.StartsWith("Pages free:")) free = ParsePages(line);
            else if (line.StartsWith("Pages inactive:")) inactive = ParsePages(line);
        }

        var freeBytes = (free + inactive) * pageSize;
        return Math.Clamp((totalBytes - freeBytes) * 100.0 / totalBytes, 0, 100);
    }

    private static long ParsePages(string line)
    {
        var digits = new string(line.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var value) ? value : 0;
    }

    private static async Task<double?> SampleBatteryPercentAsync(CancellationToken ct)
    {
        var output = await RunAsync("pmset", new[] { "-g", "batt" }, ct);
        var match = BatteryPercentRegex().Match(output);
        return match.Success && double.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    private static async Task<string> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }

    [GeneratedRegex(@"([\d.]+)%\s*idle")]
    private static partial Regex CpuIdleRegex();

    [GeneratedRegex(@"(\d+)%")]
    private static partial Regex BatteryPercentRegex();
}
