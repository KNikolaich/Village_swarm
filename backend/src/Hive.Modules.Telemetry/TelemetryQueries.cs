using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Hive.Modules.Telemetry;

public sealed record LatestValueDto(string DeviceId, string Metric, DateTimeOffset Ts, double Value);

/// <summary>One bucket of a metric over time: the mean and the extremes inside the bucket.</summary>
public sealed record SeriesPointDto(DateTimeOffset Ts, double Avg, double Min, double Max);

/// <summary>One bucket of a hornet's health reports (spec 4.3 health): signal, free memory, battery.</summary>
public sealed record HealthPointDto(DateTimeOffset Ts, double Rssi, double HeapFree, double? Vbat);

public sealed record DeviceLogDto(long Id, DateTimeOffset Ts, string Level, string Msg, JsonElement? Ctx);

/// <summary>Telemetry reads: latest values for the overview, bucketed history for charts, device logs.</summary>
public sealed class TelemetryQueries(HiveDbContext db, TimeProvider time)
{
    public const int MaxPoints = 500;
    public const int DefaultPoints = 120;
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    /// <summary>Last value of every metric of every device seen in the past week (overview tiles).</summary>
    public async Task<IReadOnlyList<LatestValueDto>> LatestAsync(string? device, CancellationToken ct)
    {
        // The ts bound keeps the scan to the newest partitions.
        var since = time.GetUtcNow().AddDays(-7);
        var rows = await db.Telemetry
            .FromSql($"""
                SELECT DISTINCT ON (device_id, metric) device_id, metric, ts, value, received_at
                FROM telemetry
                WHERE ts >= {since}
                ORDER BY device_id, metric, ts DESC
                """)
            .AsNoTracking()
            .Where(r => device == null || r.DeviceId == device)
            .ToListAsync(ct);
        return rows.Select(r => new LatestValueDto(r.DeviceId, r.Metric, r.Ts, r.Value)).ToList();
    }

    /// <summary>Resolves the requested window: defaults to the last day, never longer than a year, never empty.</summary>
    internal (DateTimeOffset From, DateTimeOffset To, double BucketS) Window(DateTimeOffset? from, DateTimeOffset? to, int? points)
    {
        var end = to ?? time.GetUtcNow();
        var start = from ?? end.AddDays(-1);
        if (start >= end)
            start = end.AddMinutes(-1);
        if (end - start > MaxRange)
            start = end - MaxRange;
        var count = Math.Clamp(points ?? DefaultPoints, 2, MaxPoints);
        // Buckets are whole minutes at least: the hornets report no faster than that.
        var bucket = Math.Max(60, Math.Ceiling((end - start).TotalSeconds / count / 60) * 60);
        return (start, end, bucket);
    }

    /// <summary>Metric of one device as buckets over a window (chart data).</summary>
    public async Task<IReadOnlyList<SeriesPointDto>> SeriesAsync(
        string device, string metric, DateTimeOffset? from, DateTimeOffset? to, int? points, CancellationToken ct)
    {
        var (start, end, bucket) = Window(from, to, points);
        // Readings before the cut exist only as hourly aggregates (retention), later ones only raw: no overlap.
        var cut = await db.Modes.AsNoTracking().Where(m => m.Key == Mode.TelemetryCut).Select(m => m.Value).FirstOrDefaultAsync(ct) is { } text
                  && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
        var rows = await db.Database.SqlQuery<SeriesRow>($"""
            WITH src AS (
                SELECT ts, value AS a, value AS lo, value AS hi, 1 AS n
                FROM telemetry
                WHERE device_id = {device} AND metric = {metric} AND ts >= {start} AND ts < {end} AND ts >= {cut}
                UNION ALL
                SELECT hour, avg, min, max, count
                FROM telemetry_hourly
                WHERE device_id = {device} AND metric = {metric} AND hour >= {start} AND hour < {end} AND hour < {cut}
            )
            SELECT date_bin(make_interval(secs => {bucket}), ts, TIMESTAMPTZ '2000-01-01') AS "Ts",
                   (sum(a * n) / sum(n))::float8 AS "Avg", min(lo) AS "Min", max(hi) AS "Max"
            FROM src
            GROUP BY 1
            ORDER BY 1
            """).ToListAsync(ct);
        return rows.Select(r => new SeriesPointDto(r.Ts, r.Avg, r.Min, r.Max)).ToList();
    }

    /// <summary>Health reports of one device as buckets over a window.</summary>
    public async Task<IReadOnlyList<HealthPointDto>> HealthAsync(
        string device, DateTimeOffset? from, DateTimeOffset? to, int? points, CancellationToken ct)
    {
        var (start, end, bucket) = Window(from, to, points);
        var rows = await db.Database.SqlQuery<HealthRow>($"""
            SELECT date_bin(make_interval(secs => {bucket}), ts, TIMESTAMPTZ '2000-01-01') AS "Ts",
                   avg(rssi)::float8 AS "Rssi", avg(heap_free)::float8 AS heap_free, avg(vbat)::float8 AS "Vbat"
            FROM device_health
            WHERE device_id = {device} AND ts >= {start} AND ts < {end}
            GROUP BY 1
            ORDER BY 1
            """).ToListAsync(ct);
        return rows.Select(r => new HealthPointDto(r.Ts, r.Rssi, r.HeapFree, r.Vbat)).ToList();
    }

    /// <summary>Newest log lines of one device first; <paramref name="level"/> narrows to one level.</summary>
    public async Task<IReadOnlyList<DeviceLogDto>> LogsAsync(string device, string? level, int? limit, CancellationToken ct)
    {
        var query = db.DeviceLogs.AsNoTracking().Where(l => l.DeviceId == device);
        if (!string.IsNullOrEmpty(level))
            query = query.Where(l => l.Level == level);
        var rows = await query.OrderByDescending(l => l.Ts).ThenByDescending(l => l.Id).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);
        return rows.Select(l => new DeviceLogDto(l.Id, l.Ts, l.Level, l.Msg, l.Ctx?.RootElement.Clone())).ToList();
    }

    private sealed record SeriesRow(DateTimeOffset Ts, double Avg, double Min, double Max);

    private sealed record HealthRow(DateTimeOffset Ts, double Rssi, double HeapFree, double? Vbat);
}
