namespace Hive.Infrastructure;

public sealed class HiveOptions
{
    public const string Section = "Hive";

    /// <summary>home | city (spec 11.3).</summary>
    public string Node { get; set; } = "home";

    public string Role { get; set; } = "active";

    /// <summary>IANA time zone of the house. Calendar days in the UI ("photos of 2026-10-02") use it.</summary>
    public string TimeZone { get; set; } = "Europe/Moscow";

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>[start, end) in UTC of a calendar day in the house time zone.</summary>
    public (DateTimeOffset From, DateTimeOffset To) DayRange(DateOnly day)
    {
        var zone = Zone;
        var start = day.ToDateTime(TimeOnly.MinValue);
        var end = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        // Npgsql writes timestamptz only from UTC offsets.
        return (new DateTimeOffset(start, zone.GetUtcOffset(start)).ToUniversalTime(),
            new DateTimeOffset(end, zone.GetUtcOffset(end)).ToUniversalTime());
    }

    public DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);
}
