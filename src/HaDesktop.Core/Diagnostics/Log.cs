using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace HaDesktop.Core.Diagnostics;

public static class Log
{
    /// <summary>
    /// Records an exception the caller is deliberately not acting on (a best-effort call, a
    /// fallback path), so "nothing happened" can still be diagnosed from a trace listener or an
    /// attached debugger instead of leaving no evidence at all.
    /// </summary>
    public static void Swallowed(Exception exception, [CallerMemberName] string member = "", [CallerFilePath] string file = "") =>
        Trace.WriteLine($"[{Path.GetFileNameWithoutExtension(file)}.{member}] {exception.GetType().Name}: {exception.Message}");
}
