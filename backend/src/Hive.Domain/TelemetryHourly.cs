namespace Hive.Domain;

/// <summary>
/// One hour of one metric of one device, kept after the raw readings are gone (spec 7.4: raw telemetry lives 90 days,
/// hourly aggregates stay for good). <see cref="Count"/> readings went into the mean.
/// </summary>
public class TelemetryHourly
{
    public required string DeviceId { get; set; }
    public required string Metric { get; set; }
    public DateTimeOffset Hour { get; set; }
    public double Avg { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public int Count { get; set; }
}
