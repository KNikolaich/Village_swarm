using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Hive.Modules.Telemetry;

public sealed record LatestValueDto(string DeviceId, string Metric, DateTimeOffset Ts, double Value);

/// <summary>Telemetry reads. Charts with aggregation (agg=5m|1h|1d) come with the meteo step (spec 14.3 #13).</summary>
public sealed class TelemetryQueries(HiveDbContext db, TimeProvider time)
{
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
}
