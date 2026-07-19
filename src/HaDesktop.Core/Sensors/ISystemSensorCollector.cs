namespace HaDesktop.Core.Sensors;

public interface ISystemSensorCollector
{
    /// <summary>
    /// CPU% is computed from the delta since the previous call, so the first
    /// call after construction always returns null for it — call periodically
    /// (e.g. every 30s) and keep the same instance around.
    /// </summary>
    Task<SensorSnapshot> CollectAsync(CancellationToken ct = default);
}
