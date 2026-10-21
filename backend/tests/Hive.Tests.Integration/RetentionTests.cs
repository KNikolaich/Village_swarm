using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Modules.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests.Integration;

/// <summary>Old photos and readings go, important ones stay, charts still reach the aggregated past.</summary>
[Collection(HiveCollection.Name)]
public sealed class RetentionTests(HiveFixture hive)
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xD9];

    private async Task<string> AddPhotoAsync(string name, MediaKind kind, EventSeverity? severity, int ageDays, bool pinned = false, int size = 4)
    {
        var ts = DateTimeOffset.UtcNow.AddDays(-ageDays);
        var path = $"photos/retention-test/{name}.jpg";
        var full = Path.Combine(hive.MediaRoot, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, size == Jpeg.Length ? Jpeg : [.. Jpeg, .. new byte[size - Jpeg.Length]]);
        await hive.QueryAsync(async db =>
        {
            string? eventId = null;
            if (severity is { } s)
            {
                eventId = Hive.Contracts.Ulid.New(ts);
                db.Events.Add(new DeviceEvent { Id = eventId, DeviceId = "retention-cam", Type = "motion", Severity = s, Ts = ts, ReceivedAt = ts, Payload = JsonDocument.Parse("{}") });
            }
            db.Media.Add(new MediaItem { Id = name, DeviceId = "retention-cam", EventId = eventId, Kind = kind, Ts = ts, ReceivedAt = ts, Path = path, Bytes = size, Sha256 = name, Pinned = pinned });
            await db.SaveChangesAsync();
            return 0;
        });
        return full;
    }

    private async Task<bool> HasRowAsync(string id) => await hive.QueryAsync(db => db.Media.AnyAsync(m => m.Id == id));

    private async Task<T> WithServiceAsync<T>(Func<RetentionService, Task<T>> run)
    {
        using var scope = hive.App.Services.CreateScope();
        return await run(scope.ServiceProvider.GetRequiredService<RetentionService>());
    }

    [Fact]
    public async Task The_daily_cleanup_removes_old_ordinary_photos_and_keeps_alarms_and_pinned_ones()
    {
        var info40 = await AddPhotoAsync("d-info40", MediaKind.Photo, EventSeverity.Info, 40);
        var info10 = await AddPhotoAsync("d-info10", MediaKind.Photo, EventSeverity.Info, 10);
        var alarm200 = await AddPhotoAsync("d-alarm200", MediaKind.Photo, EventSeverity.Alarm, 200);
        var alarm400 = await AddPhotoAsync("d-alarm400", MediaKind.Photo, EventSeverity.Alarm, 400);
        var pinned = await AddPhotoAsync("d-pinned", MediaKind.Photo, EventSeverity.Info, 100, pinned: true);
        var snapshot = await AddPhotoAsync("d-snap31", MediaKind.Snapshot, null, 31);

        var result = await WithServiceAsync(s => s.RunDailyAsync(CancellationToken.None));

        Assert.True(result.PhotosDeleted >= 3);
        foreach (var (id, file) in new[] { ("d-info40", info40), ("d-alarm400", alarm400), ("d-snap31", snapshot) })
        {
            Assert.False(await HasRowAsync(id), id);
            Assert.False(File.Exists(file), id);
        }
        foreach (var (id, file) in new[] { ("d-info10", info10), ("d-alarm200", alarm200), ("d-pinned", pinned) })
        {
            Assert.True(await HasRowAsync(id), id);
            Assert.True(File.Exists(file), id);
        }
    }

    [Fact]
    public async Task A_full_disk_goes_for_ordinary_photos_first_and_spares_young_alarms()
    {
        var info10 = await AddPhotoAsync("p-info10", MediaKind.Photo, EventSeverity.Warn, 10);
        var alarm40 = await AddPhotoAsync("p-alarm40", MediaKind.Photo, EventSeverity.Alarm, 40);
        var alarm100 = await AddPhotoAsync("p-alarm100", MediaKind.Photo, EventSeverity.Alarm, 100);
        var pinnedOld = await AddPhotoAsync("p-pinned", MediaKind.Photo, EventSeverity.Info, 300, pinned: true);

        await WithServiceAsync(s => s.DeletePhotosAsync(1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.False(await HasRowAsync("p-info10"));
        Assert.True(await HasRowAsync("p-alarm100"));

        await WithServiceAsync(s => s.DeletePhotosAsync(3, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.False(await HasRowAsync("p-alarm100"));
        Assert.True(await HasRowAsync("p-alarm40"), "an alarm photo of 40 days is not deleted even at the last step");
        Assert.True(await HasRowAsync("p-pinned"));
        Assert.False(File.Exists(info10));
        Assert.True(File.Exists(alarm40) && File.Exists(pinnedOld));
        Assert.False(File.Exists(alarm100));
    }

    /// <summary>A pretend small disk: the photos under the media root plus a fixed amount of other data (database, backups).</summary>
    private sealed class SmallDisk(string root, long total, long other) : Hive.Modules.Telemetry.IDiskSpaceReader
    {
        public Hive.Modules.Telemetry.DiskSpace? Read(string path)
        {
            var photos = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            return new Hive.Modules.Telemetry.DiskSpace(total, total - other - photos);
        }
    }

    [Fact]
    public async Task A_disk_at_90_percent_loses_only_as_many_of_the_oldest_ordinary_photos_as_it_takes_to_get_to_80()
    {
        // 30 ordinary photos of 1 KB (10 to 39 days old), 3 young alarms and one pinned photo, all on a 100 KB disk
        for (var age = 10; age < 40; age++)
            await AddPhotoAsync($"s-info{age}", MediaKind.Photo, EventSeverity.Info, age, size: 1000);
        foreach (var age in new[] { 40, 45, 50 })
            await AddPhotoAsync($"s-alarm{age}", MediaKind.Photo, EventSeverity.Alarm, age, size: 1000);
        await AddPhotoAsync("s-pinned", MediaKind.Photo, EventSeverity.Info, 300, pinned: true, size: 1000);
        const long total = 100_000;
        var inMedia = Directory.EnumerateFiles(hive.MediaRoot, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        var disk = new SmallDisk(hive.MediaRoot, total, other: 90_000 - inMedia);   // 90 % used
        Assert.Equal(90.0, disk.Read(hive.MediaRoot)!.UsedPct);

        var result = await WithServiceAsync(s => ActivatorUtilities.CreateInstance<RetentionService>(
            hive.App.Services.CreateScope().ServiceProvider, disk).RelieveDiskAsync(CancellationToken.None));

        var after = disk.Read(hive.MediaRoot)!.UsedPct;
        Assert.InRange(after, 79.0, 80.5);   // down to the target, not further
        Assert.InRange(result.PhotoBytes, 9_900, 11_000);
        // the oldest went, the youngest stayed, alarms and the pinned photo were not touched
        for (var age = 30; age < 40; age++)
            Assert.False(await HasRowAsync($"s-info{age}"), $"info {age} days");
        for (var age = 10; age < 26; age++)
            Assert.True(await HasRowAsync($"s-info{age}"), $"info {age} days");
        Assert.True(await HasRowAsync("s-alarm40") && await HasRowAsync("s-alarm45") && await HasRowAsync("s-alarm50") && await HasRowAsync("s-pinned"));
        Assert.Equal(1, result.PressureLevel);   // the first step was enough
    }

    [Fact]
    public async Task Old_health_and_log_rows_go_and_fresh_ones_stay()
    {
        var now = DateTimeOffset.UtcNow;
        await hive.QueryAsync(async db =>
        {
            db.DeviceHealth.AddRange(
                new DeviceHealth { DeviceId = "retention-hl", Ts = now.AddDays(-20), UptimeS = 1, Rssi = -60, HeapFree = 1000, ResetReason = "power" },
                new DeviceHealth { DeviceId = "retention-hl", Ts = now.AddDays(-2), UptimeS = 1, Rssi = -60, HeapFree = 1000, ResetReason = "power" });
            db.DeviceLogs.AddRange(
                new DeviceLog { DeviceId = "retention-hl", Ts = now.AddDays(-40), Level = "info", Msg = "old" },
                new DeviceLog { DeviceId = "retention-hl", Ts = now.AddDays(-2), Level = "info", Msg = "fresh" });
            await db.SaveChangesAsync();
            return 0;
        });

        await WithServiceAsync(s => s.RunDailyAsync(CancellationToken.None));

        Assert.Equal(1, await hive.QueryAsync(db => db.DeviceHealth.CountAsync(h => h.DeviceId == "retention-hl")));
        Assert.Equal(["fresh"], await hive.QueryAsync(db => db.DeviceLogs.Where(l => l.DeviceId == "retention-hl").Select(l => l.Msg).ToListAsync()));
    }

    [Fact]
    public async Task An_old_month_of_readings_becomes_hourly_rows_and_charts_still_see_it()
    {
        // January 2025: far older than the raw window. Four readings in one hour, one in another.
        await hive.QueryAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS telemetry_2025_01 PARTITION OF telemetry FOR VALUES FROM ('2025-01-01') TO ('2025-02-01')");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO telemetry (device_id, metric, ts, value, received_at) VALUES
                ('retention-old', 'temperature', '2025-01-10 10:05:00+00', 10, '2025-01-10 10:05:00+00'),
                ('retention-old', 'temperature', '2025-01-10 10:20:00+00', 20, '2025-01-10 10:20:00+00'),
                ('retention-old', 'temperature', '2025-01-10 10:35:00+00', 30, '2025-01-10 10:35:00+00'),
                ('retention-old', 'temperature', '2025-01-10 10:50:00+00', 40, '2025-01-10 10:50:00+00'),
                ('retention-old', 'temperature', '2025-01-10 14:00:00+00', 5, '2025-01-10 14:00:00+00')
                """);
            return 0;
        });

        var result = await WithServiceAsync(s => s.RunDailyAsync(CancellationToken.None));

        Assert.True(result.PartitionsAggregated >= 1);
        var rows = await hive.QueryAsync(db => db.TelemetryHourly.Where(h => h.DeviceId == "retention-old").OrderBy(h => h.Hour).ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.Equal((25.0, 10.0, 40.0, 4), (rows[0].Avg, rows[0].Min, rows[0].Max, rows[0].Count));
        Assert.Equal(0, await hive.QueryAsync(db => db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_class WHERE relname = 'telemetry_2025_01'").SingleAsync()));

        var admin = await hive.LoginAsync();
        var series = (await admin.GetFromJsonAsync<JsonElement>(
            "/api/telemetry/series?device=retention-old&metric=temperature&from=2025-01-10T00:00:00Z&to=2025-01-11T00:00:00Z&points=24")).EnumerateArray().ToList();
        Assert.Equal(2, series.Count);
        Assert.Equal(25.0, series[0].GetProperty("avg").GetDouble(), 3);
        Assert.Equal(40.0, series[0].GetProperty("max").GetDouble());

        var status = await admin.GetFromJsonAsync<JsonElement>("/api/retention");
        Assert.Equal(DateTimeOffset.Parse("2025-02-01T00:00:00Z"), status.GetProperty("telemetryHourlyBefore").GetDateTimeOffset());
    }
}
