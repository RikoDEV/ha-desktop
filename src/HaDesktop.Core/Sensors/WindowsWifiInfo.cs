using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace HaDesktop.Core.Sensors;

/// <summary>
/// SSID/BSSID/connection type via <c>netsh wlan show interfaces</c> — same "shell out and parse
/// text" approach already used for nvidia-smi (Windows/Linux) and macOS's top/pmset, rather than
/// the much heavier WLAN API (wlanapi.dll) P/Invoke surface for the same three fields.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsWifiInfo
{
    public static async Task<(string? Ssid, string? Bssid)> GetWifiInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var output = await RunAsync("netsh", new[] { "wlan", "show", "interfaces" }, ct);

            // Deliberately not gating on a localized "State: connected" line — most of netsh's
            // field labels translate with the OS display language (this machine's is Polish), but
            // "SSID"/"BSSID" themselves are technical terms that stay untranslated across locales,
            // and netsh only emits an SSID line at all when actually associated with a network —
            // so its mere presence is itself already a locale-proof "connected" signal.
            var ssid = SsidRegex().Match(output) is { Success: true } ssidMatch ? ssidMatch.Groups[1].Value.Trim() : null;
            if (ssid is null) return (null, null);

            var bssid = BssidRegex().Match(output) is { Success: true } bssidMatch ? bssidMatch.Groups[1].Value.Trim() : null;
            return (ssid, bssid);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// "Wi-Fi", "Ethernet", or null if nothing active — Wi-Fi wins if both happen to be up, matching
    /// what actually carries traffic on a laptop docked with both. Takes the already-fetched SSID
    /// (from <see cref="GetWifiInfoAsync"/>) rather than re-running netsh a second time per poll.
    /// </summary>
    public static string? GetConnectionType(string? ssid)
    {
        if (ssid is not null) return "Wi-Fi";

        var hasEthernet = NetworkInterface.GetAllNetworkInterfaces().Any(n =>
            n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel));

        return hasEthernet ? "Ethernet" : null;
    }

    private static async Task<string> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        // CreateNoWindow matters here specifically because this runs on a 30s timer (AppSettings'
        // sensor push loop) — without it, every single poll flashes a visible console window, since
        // this GUI app has no console of its own for the child process to attach to.
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return output;
    }

    // "^\s*SSID\s*:" would also match a "SSID BSSID 1"-style scan-results line format from other
    // netsh subcommands, but "show interfaces" only ever prints a single plain "SSID" field for
    // the currently associated network, so no negative lookahead is needed here.
    [GeneratedRegex(@"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex SsidRegex();

    [GeneratedRegex(@"^\s*BSSID\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BssidRegex();
}
