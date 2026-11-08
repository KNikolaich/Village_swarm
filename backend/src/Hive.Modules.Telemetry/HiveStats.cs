namespace Hive.Modules.Telemetry;

/// <summary>The hive's own latest readings (CPU, load, memory, disk), kept for whoever tells the other node about them.</summary>
public sealed class HiveStats
{
    private readonly Lock _gate = new();
    private DateTimeOffset? _at;
    private IReadOnlyDictionary<string, double> _values = new Dictionary<string, double>();

    public void Update(DateTimeOffset at, IEnumerable<(string Metric, double Value)> values)
    {
        lock (_gate)
        {
            _at = at;
            _values = values.ToDictionary(v => v.Metric, v => v.Value);
        }
    }

    public (DateTimeOffset? At, IReadOnlyDictionary<string, double> Values) Latest()
    {
        lock (_gate)
            return (_at, _values);
    }
}
