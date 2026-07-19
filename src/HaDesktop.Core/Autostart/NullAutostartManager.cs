namespace HaDesktop.Core.Autostart;

public sealed class NullAutostartManager : IAutostartManager
{
    public Task<bool> IsEnabledAsync() => Task.FromResult(false);
    public Task SetEnabledAsync(bool enabled) => Task.CompletedTask;
}
