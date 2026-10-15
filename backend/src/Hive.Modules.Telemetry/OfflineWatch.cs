using System.Text.Json;
using Hive.Contracts;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Telemetry;

public sealed class OfflineWatchOptions
{
    public const string Section = "Hive:OfflineWatch";

    public bool Enabled { get; set; } = true;

    /// <summary>A hornet that has been offline this long is reported (spec 14.3 built-in rule: offline > N minutes → warn).</summary>
    public int Minutes { get; set; } = 15;
}

/// <summary>
/// Tells the family when a hornet has been gone for a quarter of an hour (a warn event, so it reaches Telegram), and when
/// it is back. A short drop (a reboot, a Wi-Fi hiccup) passes unnoticed. Deep sleepers are marked offline only after they
/// miss several wake-ups, so this waits on top of that.
/// </summary>
public sealed class OfflineWatch(IServiceScopeFactory scopes, IngestQueue ingest, IOptions<OfflineWatchOptions> options, TimeProvider time, ILogger<OfflineWatch> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The build runs the app to write the OpenAPI document; it must not need a database.
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider" || !options.Value.Enabled)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Offline check failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task CheckAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var now = time.GetUtcNow();
        var limit = TimeSpan.FromMinutes(Math.Max(1, options.Value.Minutes));

        var gone = await db.Devices
            .Where(d => d.Status == DeviceStatus.Offline && d.OfflineReportedAt == null && d.LastSeenAt != null && now - d.LastSeenAt >= limit)
            .ToListAsync(ct);
        foreach (var device in gone)
        {
            device.OfflineReportedAt = now;
            var payload = JsonSerializer.SerializeToDocument(new { since = device.LastSeenAt, minutes = (int)(now - device.LastSeenAt!.Value).TotalMinutes });
            ingest.Enqueue(new DeviceEventReceived(device.DeviceId, Ulid.New(now), now, now, "device_offline", EventSeverity.Warn, null, payload));
        }

        var back = await db.Devices.Where(d => d.Status == DeviceStatus.Online && d.OfflineReportedAt != null).ToListAsync(ct);
        foreach (var device in back)
        {
            var downFor = (int)(now - device.OfflineReportedAt!.Value).TotalMinutes;
            device.OfflineReportedAt = null;
            ingest.Enqueue(new DeviceEventReceived(device.DeviceId, Ulid.New(now), now, now, "device_online", EventSeverity.Info, null,
                JsonSerializer.SerializeToDocument(new { reportedMinutesAgo = downFor })));
        }
        await db.SaveChangesAsync(ct);
    }
}
