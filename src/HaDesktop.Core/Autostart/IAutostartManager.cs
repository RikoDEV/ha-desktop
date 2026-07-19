namespace HaDesktop.Core.Autostart;

public interface IAutostartManager
{
    Task<bool> IsEnabledAsync();
    Task SetEnabledAsync(bool enabled);
}
