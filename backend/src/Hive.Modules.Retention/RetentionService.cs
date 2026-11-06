using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Modules.Media;
using Hive.Modules.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Retention;

/// <summary>What one cleanup removed. <see cref="PartitionsAggregated"/> are whole months of raw readings turned into hourly rows.</summary>
public sealed record RetentionResult(
    int PhotosDeleted, long PhotoBytes, int PartitionsAggregated, int HealthRows, int LogRows, int OtherRows, int PressureLevel = 0)
{
    public static RetentionResult operator +(RetentionResult a, RetentionResult b) => new(
        a.PhotosDeleted + b.PhotosDeleted, a.PhotoBytes + b.PhotoBytes, a.PartitionsAggregated + b.PartitionsAggregated,
        a.HealthRows + b.HealthRows, a.LogRows + b.LogRows, a.OtherRows + b.OtherRows, Math.Max(a.PressureLevel, b.PressureLevel));
}

/// <summary>Disk size and use, and the part of it that is photos and the database, in bytes.</summary>
public sealed record DiskUsage(long? TotalBytes, long? UsedBytes, long PhotoBytes, long DatabaseBytes);

/// <summary>The daily cleanup (spec 7.4) and the emergency one when the disk fills up.</summary>
public sealed partial class RetentionService(
    HiveDbContext db, MediaStore media, IDiskSpaceReader disks, IOptions<RetentionOptions> options, TimeProvider time, ILogger<RetentionService> logger)
{
    public const string LastRunKey = "retention.last";
    private const int Batch = 500;

    [GeneratedRegex(@"^telemetry_(\d{4})_(\d{2})$")]
    private static partial Regex PartitionName();

    public double? DiskUsedPct() => disks.Read(media.Root)?.UsedPct;

    /// <summary>
    /// What fills the disk: photos and the database are what the hive can measure; the rest (the system, old hive
    /// versions in Docker, backups) is whatever is left of the used space. Shows the family what a cleanup can help with.
    /// </summary>
    public async Task<DiskUsage> UsageAsync(CancellationToken ct)
    {
        var disk = disks.Read(media.Root);
        var photos = await db.Media.SumAsync(m => (long?)m.Bytes, ct) ?? 0;
        var database = await db.Database.SqlQuery<long>($"""
            SELECT pg_database_size(current_database()) AS "Value"
            """).SingleAsync(ct);
        return new DiskUsage(disk?.Total, disk is null ? null : disk.Total - disk.Free, photos, database);
    }

    /// <summary>The normal schedule: photos by age, old months of readings into hourly rows, health, logs, housekeeping.</summary>
    public async Task<RetentionResult> RunDailyAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        var result = await DeletePhotosAsync(0, now, ct);
        result += await AggregateTelemetryAsync(now, ct);

        var health = await db.Database.ExecuteSqlAsync($"DELETE FROM device_health WHERE ts < {now.AddDays(-o.HealthDays)}", ct);
        var logs = await db.Database.ExecuteSqlAsync($"DELETE FROM device_logs WHERE ts < {now.AddDays(-o.LogDays)}", ct);
        var old = now.AddDays(-o.HousekeepingDays);
        var other = await db.Database.ExecuteSqlAsync($"""
            DELETE FROM notifications WHERE created_at < {old} AND status <> 'Pending'
            """, ct);
        other += await db.Database.ExecuteSqlAsync($"DELETE FROM rule_runs WHERE ts < {old}", ct);
        other += await db.Database.ExecuteSqlAsync($"DELETE FROM commands WHERE issued_at < {old} AND status <> 'Sent'", ct);
        other += await db.Database.ExecuteSqlAsync($"DELETE FROM backup_runs WHERE started_at < {now.AddDays(-365)}", ct);
        result += new RetentionResult(0, 0, 0, health, logs, other);

        await RememberAsync(result, now, ct);
        Audit.Record(db, now, "system:retention", "retention", null, result);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Retention: {Photos} photos ({Bytes} bytes), {Months} months of readings aggregated, {Health} health rows, {Logs} log rows",
            result.PhotosDeleted, result.PhotoBytes, result.PartitionsAggregated, result.HealthRows, result.LogRows);
        return result;
    }

    /// <summary>
    /// The disk is above its limit: step by step, ordinary photos first, until it is below the target or nothing more
    /// may go (pinned photos and young alarm photos stay). Each step removes the oldest eligible photos and stops as soon
    /// as the disk would be at its target, so a disk that is full of something else (the database, backups) does not
    /// cost the family all its photos.
    /// </summary>
    public async Task<RetentionResult> RelieveDiskAsync(CancellationToken ct)
    {
        var o = options.Value;
        var total = new RetentionResult(0, 0, 0, 0, 0, 0);
        for (var level = 1; level <= RetentionPolicy.MaxPressureLevel; level++)
        {
            if (disks.Read(media.Root) is not { } disk || (level == 1 ? disk.UsedPct < o.DiskHighPct : disk.UsedPct <= o.DiskTargetPct))
                break;
            // What has to leave the disk to bring it down to the target; a photo goes whole, so a little more may.
            var budget = (long)Math.Ceiling((disk.UsedPct - o.DiskTargetPct) / 100.0 * disk.Total);
            var step = await DeletePhotosAsync(level, time.GetUtcNow(), ct, budget);
            total += step with { PressureLevel = step.PhotosDeleted > 0 ? level : 0 };
        }
        if (total.PhotosDeleted > 0)
        {
            var now = time.GetUtcNow();
            Audit.Record(db, now, "system:retention", "disk_cleanup", null, total);
            await db.SaveChangesAsync(ct);
            logger.LogWarning("The disk was above {High} %: removed {Photos} photos ({Bytes} bytes), pressure level {Level}",
                o.DiskHighPct, total.PhotosDeleted, total.PhotoBytes, total.PressureLevel);
        }
        return total;
    }

    /// <summary>Deletes the photos the policy lets go at <paramref name="level"/>: files first, then rows, in batches.</summary>
    internal async Task<RetentionResult> DeletePhotosAsync(int level, DateTimeOffset now, CancellationToken ct, long? budgetBytes = null)
    {
        var o = options.Value;
        var oldest = now - RetentionPolicy.YoungestDeletable(level, o);
        var deleted = 0;
        long bytes = 0;
        DateTimeOffset? lastTs = null;
        string? lastId = null;

        while (true)
        {
            var page = await (
                from m in db.Media.AsNoTracking()
                where !m.Pinned && m.Ts < oldest
                      && (lastTs == null || m.Ts > lastTs || (m.Ts == lastTs && string.Compare(m.Id, lastId) > 0))
                join e in db.Events.AsNoTracking() on m.EventId equals e.Id into events
                from e in events.DefaultIfEmpty()
                orderby m.Ts, m.Id
                select new { m.Id, m.Path, m.ThumbPath, m.Kind, m.Ts, m.Bytes, Severity = (EventSeverity?)e.Severity })
                .Take(Batch).ToListAsync(ct);
            if (page.Count == 0)
                break;
            (lastTs, lastId) = (page[^1].Ts, page[^1].Id);

            var ids = new List<string>();
            var enough = false;
            foreach (var p in page.Where(p => RetentionPolicy.ShouldDelete(false, p.Kind, p.Severity, p.Ts, now, level, o)))
            {
                DeleteFile(p.Path);
                if (p.ThumbPath is not null)
                    DeleteFile(p.ThumbPath);
                bytes += p.Bytes;
                ids.Add(p.Id);
                if (budgetBytes is { } budget && bytes >= budget)
                {
                    enough = true;
                    break;
                }
            }
            if (ids.Count > 0)
                deleted += await db.Media.Where(m => ids.Contains(m.Id)).ExecuteDeleteAsync(ct);
            if (enough)
                break;
        }
        return new RetentionResult(deleted, bytes, 0, 0, 0, 0);
    }

    private void DeleteFile(string relative)
    {
        try
        {
            File.Delete(media.FullPath(relative));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning("Could not delete {Path}: {Reason}", relative, e.Message);
        }
    }

    /// <summary>
    /// Months of raw readings that are entirely older than the raw window become hourly rows, then the month's
    /// partition is dropped (cheap, no vacuum). Readings with nonsense timestamps in the default partition just go.
    /// </summary>
    internal async Task<RetentionResult> AggregateTelemetryAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-options.Value.RawTelemetryDays);
        var names = await db.Database.SqlQuery<string>($"""
            SELECT c.relname AS "Value" FROM pg_inherits i
            JOIN pg_class c ON c.oid = i.inhrelid JOIN pg_class p ON p.oid = i.inhparent
            WHERE p.relname = 'telemetry'
            """).ToListAsync(ct);

        var aggregated = 0;
        DateTimeOffset? cut = null;
        foreach (var name in names.Order())
        {
            var m = PartitionName().Match(name);
            if (!m.Success)
                continue;
            var from = new DateTimeOffset(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), 1, 0, 0, 0, TimeSpan.Zero);
            var end = from.AddMonths(1);
            if (end > cutoff)
                continue;

            // The partition name comes from the catalog and matched the pattern above: it is not user input.
#pragma warning disable EF1002
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync($"""
                INSERT INTO telemetry_hourly (device_id, metric, hour, avg, min, max, count)
                SELECT device_id, metric, date_trunc('hour', ts AT TIME ZONE 'UTC') AT TIME ZONE 'UTC',
                       avg(value), min(value), max(value), count(*)
                FROM {name} GROUP BY 1, 2, 3
                ON CONFLICT (device_id, metric, hour) DO UPDATE SET
                    avg = (telemetry_hourly.avg * telemetry_hourly.count + EXCLUDED.avg * EXCLUDED.count) / (telemetry_hourly.count + EXCLUDED.count),
                    min = LEAST(telemetry_hourly.min, EXCLUDED.min),
                    max = GREATEST(telemetry_hourly.max, EXCLUDED.max),
                    count = telemetry_hourly.count + EXCLUDED.count
                """, ct);
            await db.Database.ExecuteSqlRawAsync($"DROP TABLE {name}", ct);
#pragma warning restore EF1002
            await tx.CommitAsync(ct);
            aggregated++;
            cut = end;
        }

        // Moved forward only after the data is safe in the hourly table.
        if (cut is { } newCut)
        {
            var row = await db.Modes.FirstOrDefaultAsync(x => x.Key == Mode.TelemetryCut, ct);
            var text = newCut.ToString("O", CultureInfo.InvariantCulture);
            if (row is null)
                db.Modes.Add(new Mode { Key = Mode.TelemetryCut, Value = text, UpdatedAt = now, UpdatedBy = "system:retention" });
            else if (!DateTimeOffset.TryParse(row.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var old) || newCut > old)
            {
                row.Value = text;
                row.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
        }

        var strays = await db.Database.ExecuteSqlAsync($"DELETE FROM telemetry_default WHERE ts < {cutoff}", ct);
        return new RetentionResult(0, 0, aggregated, 0, 0, strays);
    }

    private async Task RememberAsync(RetentionResult result, DateTimeOffset now, CancellationToken ct)
    {
        var text = JsonSerializer.Serialize(new { at = now, photos = result.PhotosDeleted, bytes = result.PhotoBytes, months = result.PartitionsAggregated });
        var row = await db.Modes.FirstOrDefaultAsync(m => m.Key == LastRunKey, ct);
        if (row is null)
            db.Modes.Add(new Mode { Key = LastRunKey, Value = text, UpdatedAt = now, UpdatedBy = "system:retention" });
        else
        {
            row.Value = text;
            row.UpdatedAt = now;
        }
    }
}
