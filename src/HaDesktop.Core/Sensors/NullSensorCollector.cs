namespace HaDesktop.Core.Sensors;

public sealed class NullSensorCollector : ISystemSensorCollector
{
    public Task<SensorSnapshot> CollectAsync(CancellationToken ct = default) =>
        Task.FromResult(new SensorSnapshot(null, null, null, null, null, null, null, null));
}
