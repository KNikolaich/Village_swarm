using System.Globalization;
using System.Text.Json;
using Hive.Contracts;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Hive.Modules.Telemetry;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Retention;

public sealed record RetentionStatusDto(
    double? DiskUsedPct, double DiskHighPct, DateTimeOffset? LastRunAt, int LastPhotosDeleted, long LastPhotoBytes,
    DateTimeOffset? TelemetryHourlyBefore, RetentionOptions Settings,
    long? DiskTotalBytes, long? DiskUsedBytes, long PhotoBytes, long DatabaseBytes);

/// <summary>
/// Looks after the disk (spec 7.4): the daily cleanup once a night after the backup, and every few minutes a look at
/// how full the disk is, with an emergency cleanup (and a warning to the family) when it is above the limit.
/// </summary>
public sealed class RetentionJob(
    IServiceScopeFactory scopes, IngestQueue ingest, IOptions<RetentionOptions> options, IOptions<HiveOptions> hive, TimeProvider time, ILogger<RetentionJob> logger)
    : BackgroundService
{
    internal const string DayKey = "retention.day";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The build runs the app to write the OpenAPI document; it must not need a database.
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.DiskCheckMinutes)), time);
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken);
            do
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Retention round failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<RetentionService>();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var now = time.GetUtcNow();
        var local = hive.Value.ToLocal(now);

        // Once a day, from RunAtHour on; the day it last ran is remembered, so a restart does not repeat it.
        var today = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var marker = await db.Modes.FirstOrDefaultAsync(m => m.Key == DayKey, ct);
        if (local.Hour >= options.Value.RunAtHour && marker?.Value != today)
        {
            if (marker is null)
                db.Modes.Add(new Mode { Key = DayKey, Value = today, UpdatedAt = now, UpdatedBy = "system:retention" });
            else
            {
                marker.Value = today;
                marker.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
            await service.RunDailyAsync(ct);
        }

        if (service.DiskUsedPct() is { } used && used >= options.Value.DiskHighPct)
        {
            var result = await service.RelieveDiskAsync(ct);
            if (result.PhotosDeleted > 0)
            {
                var deviceId = $"hive-{hive.Value.Node}";
                // Where the disk ended up: when it is still above the limit, photos were not what filled it.
                var after = service.DiskUsedPct();
                var payload = JsonSerializer.SerializeToDocument(new
                {
                    photos = result.PhotosDeleted, bytes = result.PhotoBytes, level = result.PressureLevel, usedPct = used,
                    usedAfterPct = after, stillFull = after >= options.Value.DiskHighPct,
                });
                ingest.Enqueue(new DeviceEventReceived(deviceId, Ulid.New(now), now, now, "disk_cleanup", EventSeverity.Warn, null, payload));
            }
        }
    }
}

public static class RetentionModule
{
    public const string Name = "retention";

    public static IServiceCollection AddRetentionModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.Section));
        services.TryAddSingleton<IDiskSpaceReader, SystemDiskSpaceReader>();
        services.AddScoped<RetentionService>();
        services.AddHostedService<RetentionJob>();
        return services;
    }

    public static IEndpointRouteBuilder MapRetentionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/retention", async (HiveDbContext db, RetentionService service, IOptions<RetentionOptions> options, CancellationToken ct) =>
            {
                var modes = await db.Modes.AsNoTracking().Where(m => m.Key == RetentionService.LastRunKey || m.Key == Mode.TelemetryCut).ToDictionaryAsync(m => m.Key, m => m.Value, ct);
                DateTimeOffset? lastAt = null;
                var photos = 0;
                long bytes = 0;
                if (modes.TryGetValue(RetentionService.LastRunKey, out var last))
                {
                    using var doc = JsonDocument.Parse(last);
                    lastAt = doc.RootElement.GetProperty("at").GetDateTimeOffset();
                    photos = doc.RootElement.GetProperty("photos").GetInt32();
                    bytes = doc.RootElement.GetProperty("bytes").GetInt64();
                }
                DateTimeOffset? cut = modes.TryGetValue(Mode.TelemetryCut, out var c)
                    && DateTimeOffset.TryParse(c, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
                var usage = await service.UsageAsync(ct);
                return new RetentionStatusDto(service.DiskUsedPct(), options.Value.DiskHighPct, lastAt, photos, bytes, cut, options.Value,
                    usage.TotalBytes, usage.UsedBytes, usage.PhotoBytes, usage.DatabaseBytes);
            })
            .WithTags("retention")
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }
}
