using System.Collections.Concurrent;

namespace CanSensorHub.Core.Logging;

/// <summary>
/// Keeps the most recent value of every named measurement a device client has decoded (telemetry
/// channels, per-sensor quantities, status fields, ...), each stamped with the time it arrived. Backs
/// both live dashboard tiles and <see cref="WideCsvLogger"/> exports.
/// </summary>
public sealed class LiveValueStore
{
    private readonly ConcurrentDictionary<string, (double Value, DateTimeOffset Timestamp)> _latest = new();

    public void Set(string key, double value) => _latest[key] = (value, DateTimeOffset.Now);

    public bool TryGet(string key, out double value)
    {
        if (_latest.TryGetValue(key, out var entry))
        {
            value = entry.Value;
            return true;
        }
        value = 0;
        return false;
    }

    public (double Value, DateTimeOffset Timestamp)? TryGetWithTimestamp(string key) =>
        _latest.TryGetValue(key, out var entry) ? entry : null;

    /// <summary>Snapshot of every key whose last update is no older than <paramref name="maxAge"/>.</summary>
    public IReadOnlyDictionary<string, double> Snapshot(TimeSpan maxAge)
    {
        var now = DateTimeOffset.Now;
        var result = new Dictionary<string, double>();
        foreach (var (key, (value, ts)) in _latest)
            if (now - ts <= maxAge)
                result[key] = value;
        return result;
    }

    public IReadOnlyCollection<string> Keys => _latest.Keys.ToArray();
}
