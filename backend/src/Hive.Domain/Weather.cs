using System.Text.Json;

namespace Hive.Domain;

/// <summary>
/// The last forecast the hive fetched (one row, Id = 1), kept so a restart or a day without the weather service
/// still has something to show and to base decisions on. <see cref="Payload"/> is the module's own JSON.
/// </summary>
public class WeatherSnapshot
{
    public const int SingleId = 1;

    public int Id { get; set; } = SingleId;
    public DateTimeOffset FetchedAt { get; set; }
    public required JsonDocument Payload { get; set; }
}
