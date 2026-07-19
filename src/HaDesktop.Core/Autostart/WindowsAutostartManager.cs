using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HaDesktop.Core.Autostart;

[SupportedOSPlatform("windows")]
public sealed class WindowsAutostartManager : IAutostartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HaDesktop";

    public Task<bool> IsEnabledAsync()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return Task.FromResult(key?.GetValue(ValueName) is string);
    }

    public Task SetEnabledAsync(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine the running executable's path.");
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        return Task.CompletedTask;
    }
}
