using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using HaDesktop.Core.Diagnostics;
using HaDesktop.Core.Ha;

namespace HaDesktop.Tray;

/// <summary>
/// Service calls made from tiles and their detail popups. Each call goes through whichever client
/// the session has at that moment — a tile outlives reconnects, so it must not hold on to the
/// client it was created under. All best effort: the tile resyncs from the next state_changed event.
/// </summary>
internal static class HaActions
{
    public static async Task CallAsync(string domain, string service, string entityId, JsonObject? data = null)
    {
        if (HaSession.Client is not { } client) return;

        try { await client.CallServiceAsync(domain, service, entityId, data); }
        catch (Exception ex) { Log.Swallowed(ex); /* best effort — the tile resyncs from the next state_changed event */ }
    }

    public static Task ToggleAsync(HaEntityState state) => CallAsync(state.Domain, "toggle", state.EntityId);

    /// <summary>The toggle-or-cycle action a small tap performs: open/close for a cover (a toggle is ambiguous mid-travel), toggle for everything else.</summary>
    public static Task QuickActionAsync(HaEntityState state)
    {
        if (state.Domain != "cover") return ToggleAsync(state);

        var isOpen = state.State is "open" or "opening";
        return CallAsync("cover", isOpen ? "close_cover" : "open_cover", state.EntityId);
    }
}
