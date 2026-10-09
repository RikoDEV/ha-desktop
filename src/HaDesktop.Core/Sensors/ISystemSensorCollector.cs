using HaDesktop.Core.Storage;

namespace HaDesktop.Core.Sensors;

public interface ISystemSensorCollector
{
    /// <summary>
    /// Samples only the readings <paramref name="prefs"/> shares; everything else comes back null
    /// without being measured at all — several of them cost a child process or a COM round trip.
    /// CPU% and the other rate-based readings are computed from the delta since the previous call,
    /// so the first call after one is enabled always returns null for it — call periodically
    /// (e.g. every 30s) and keep the same instance around.
    /// </summary>
    Task<SensorSnapshot> CollectAsync(SensorPreferences prefs, CancellationToken ct = default);
}
