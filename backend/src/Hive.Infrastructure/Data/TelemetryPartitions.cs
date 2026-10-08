using Microsoft.EntityFrameworkCore;

namespace Hive.Infrastructure.Data;

/// <summary>
/// Monthly partitions of <c>telemetry</c> (spec 6.4). Partitions are created ahead of time;
/// rows with odd timestamps (clock never synced) land in <c>telemetry_default</c>.
/// </summary>
public static class TelemetryPartitions
{
    public const string CreateTableSql = """
        CREATE TABLE telemetry (
            device_id   varchar(32)      NOT NULL,
            metric      varchar(32)      NOT NULL,
            ts          timestamptz      NOT NULL,
            value       double precision NOT NULL,
            received_at timestamptz      NOT NULL
        ) PARTITION BY RANGE (ts);
        CREATE TABLE telemetry_default PARTITION OF telemetry DEFAULT;
        CREATE INDEX ix_telemetry_device_metric_ts ON telemetry (device_id, metric, ts);
        CREATE INDEX ix_telemetry_ts_brin ON telemetry USING brin (ts);
        """;

    public const string DropTableSql = "DROP TABLE telemetry;";

    public static string PartitionName(DateOnly month) => $"telemetry_{month:yyyy_MM}";

    /// <summary>Creates partitions for the given month and the next <paramref name="ahead"/> months.</summary>
    public static async Task EnsureAsync(HiveDbContext db, DateTimeOffset now, int ahead = 2, CancellationToken ct = default)
    {
        var first = new DateOnly(now.UtcDateTime.Year, now.UtcDateTime.Month, 1);
        for (var i = 0; i <= ahead; i++)
        {
            var from = first.AddMonths(i);
            var to = from.AddMonths(1);
            // Names and dates are generated here, not user input.
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync($"""
                CREATE TABLE IF NOT EXISTS {PartitionName(from)} PARTITION OF telemetry
                    FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}');
                """, ct);
#pragma warning restore EF1002
        }
    }
}
