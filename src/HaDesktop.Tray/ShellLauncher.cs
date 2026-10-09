using System;
using System.Diagnostics;
using HaDesktop.Core.Diagnostics;

namespace HaDesktop.Tray;

public static class ShellLauncher
{
    /// <summary>Opens a URL (or any URI with a registered handler) in whatever the OS associates with it. Best effort: if nothing handles it, there's nothing sensible to do.</summary>
    public static void TryOpen(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Swallowed(ex); /* best effort — no default handler for this URI */ }
    }
}
