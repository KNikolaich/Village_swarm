using System.Globalization;
using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Weather;

public sealed class WeatherOptions
{
    public const string Section = "Weather";

    /// <summary>Open-Meteo to ask unless Settings say otherwise; a proxy of it works the same.</summary>
    public string BaseUrl { get; set; } = "https://api.open-meteo.com";

    public int RefreshMinutes { get; set; } = 30;
}

/// <summary>The place and the server of the weather, kept in the hive's shared settings (modes table).</summary>
public sealed class WeatherSettingsService(HiveDbContext db, IOptions<WeatherOptions> options, IOptions<HiveOptions> hive, TimeProvider time)
{
    private const string UrlKey = "weather.url";
    private const string LatKey = "weather.lat";
    private const string LonKey = "weather.lon";
    private const string ZoneKey = "weather.tz";

    public async Task<WeatherSettingsDto> GetAsync(CancellationToken ct)
    {
        var rows = await db.Modes.AsNoTracking().Where(m => m.Key.StartsWith("weather.")).ToDictionaryAsync(m => m.Key, m => m.Value, ct);
        double? Number(string key) =>
            rows.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
        return new WeatherSettingsDto(
            rows.GetValueOrDefault(UrlKey) is { Length: > 0 } url ? url : options.Value.BaseUrl,
            Number(LatKey), Number(LonKey),
            rows.GetValueOrDefault(ZoneKey) is { Length: > 0 } tz ? tz : hive.Value.TimeZone);
    }

    /// <summary>Null when the settings are fine, otherwise what is wrong with them.</summary>
    public static string? Validate(WeatherSettingsDto s)
    {
        if (s.Latitude is not (>= -90 and <= 90) || s.Longitude is not (>= -180 and <= 180))
            return "Latitude is -90..90 and longitude -180..180";
        if (!Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "The weather server address must be http(s)://...";
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(s.Timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return "Unknown time zone";
        }
        return null;
    }

    public async Task SetAsync(WeatherSettingsDto s, string by, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var values = new Dictionary<string, string>
        {
            [UrlKey] = s.BaseUrl.TrimEnd('/'),
            [LatKey] = s.Latitude!.Value.ToString("0.#####", CultureInfo.InvariantCulture),
            [LonKey] = s.Longitude!.Value.ToString("0.#####", CultureInfo.InvariantCulture),
            [ZoneKey] = s.Timezone,
        };
        foreach (var (key, value) in values)
        {
            var row = await db.Modes.FirstOrDefaultAsync(m => m.Key == key, ct);
            if (row is null)
                db.Modes.Add(new Mode { Key = key, Value = value, UpdatedAt = now, UpdatedBy = by });
            else
            {
                row.Value = value;
                row.UpdatedAt = now;
                row.UpdatedBy = by;
            }
        }
        // The address of a private proxy stays out of the log: only the place is recorded.
        Audit.Record(db, now, by, "set_weather", null, new { s.Latitude, s.Longitude, s.Timezone });
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Why the last refresh failed, shared by all requests (null: it worked or has not run).</summary>
public sealed class WeatherState
{
    public volatile string? Error;
}

/// <summary>Fetches the forecast, keeps the last good one in the database and serves it.</summary>
public sealed class WeatherService(
    HiveDbContext db, WeatherSettingsService settings, OpenMeteoClient client, WeatherState state, TimeProvider time,
    ILogger<WeatherService> logger)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(3);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<WeatherDto> GetAsync(CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        var configured = s.Latitude is not null && s.Longitude is not null;
        var snapshot = await db.WeatherSnapshots.AsNoTracking().FirstOrDefaultAsync(ct);
        if (snapshot is null)
            return new WeatherDto(configured, null, false, state.Error, null, [], []);
        var forecast = snapshot.Payload.Deserialize<WeatherForecast>(Json)!;
        return new WeatherDto(configured, snapshot.FetchedAt, time.GetUtcNow() - snapshot.FetchedAt > StaleAfter, state.Error,
            forecast.Current, forecast.Hourly, forecast.Daily);
    }

    /// <summary>Asks the weather server and stores the answer; a failure is remembered and the old forecast stays.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        if (s.Latitude is null || s.Longitude is null)
            return false;
        try
        {
            var forecast = await client.FetchAsync(s, ct);
            var payload = JsonSerializer.SerializeToDocument(forecast, Json);
            var row = await db.WeatherSnapshots.FirstOrDefaultAsync(ct);
            var now = time.GetUtcNow();
            if (row is null)
                db.WeatherSnapshots.Add(new WeatherSnapshot { FetchedAt = now, Payload = payload });
            else
            {
                row.FetchedAt = now;
                row.Payload = payload;
            }
            await db.SaveChangesAsync(ct);
            state.Error = null;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            if (ct.IsCancellationRequested)
                throw;
            state.Error = ex.Message;
            logger.LogWarning("Weather refresh failed: {Reason}", ex.Message);
            return false;
        }
    }
}

/// <summary>Questions the automation will ask the forecast (spec 6.6): rain lately or soon, sun times of a day.</summary>
public static class ForecastMath
{
    /// <summary>Total rain in mm over [from, to) and the highest probability among those hours.</summary>
    public static (double Millimetres, int? MaxProbability) Rain(IEnumerable<WeatherHourDto> hours, DateTimeOffset from, DateTimeOffset to)
    {
        var inside = hours.Where(h => h.Time >= from && h.Time < to).ToList();
        return (inside.Sum(h => h.Precipitation), inside.Max(h => h.PrecipitationProbability));
    }

    public static (DateTimeOffset Sunrise, DateTimeOffset Sunset)? Sun(IEnumerable<WeatherDayDto> days, DateOnly date) =>
        days.FirstOrDefault(d => d.Date == date) is { Sunrise: { } rise, Sunset: { } set } ? (rise, set) : null;

    /// <summary>
    /// Whether it is dark at <paramref name="now"/>: after sunset and before the next sunrise. Null when the data does
    /// not cover that moment (no sun times, or now is before the first sunrise or after the last sunset on record).
    /// </summary>
    public static bool? IsDark(IEnumerable<WeatherDayDto> days, DateTimeOffset now)
    {
        var sun = days.Where(d => d.Sunrise is not null && d.Sunset is not null).ToList();
        if (sun.Count == 0 || now < sun.Min(d => d.Sunrise) || now >= sun.Max(d => d.Sunset))
            return null;
        return !sun.Any(d => now >= d.Sunrise && now < d.Sunset);
    }
}
