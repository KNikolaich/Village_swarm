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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Backup;

public sealed record BackupRunDto(long Id, string Kind, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool Ok, long? Bytes, string? Detail);

/// <summary>
/// <see cref="Problems"/> is what is wrong now (dump_failed, dump_stale, offsite_failed, verify_failed), empty when
/// all is well. <see cref="Dump"/>, <see cref="Offsite"/> and <see cref="Verify"/> are the latest run of each kind
/// (null: never run; no off-site runs at all means no off-site copy is configured).
/// </summary>
public sealed record BackupStatusDto(
    bool Ok, IReadOnlyList<string> Problems, DateTimeOffset? LastGoodDumpAt,
    BackupRunDto? Dump, BackupRunDto? Offsite, BackupRunDto? Verify, IReadOnlyList<BackupRunDto> Recent);

public sealed class BackupOptions
{
    public const string Section = "Backup";

    /// <summary>A dump older than this (or none at all after this long up) is a problem (spec 10.2: 36 hours).</summary>
    public int StaleAfterHours { get; set; } = 36;

    public int CheckEveryMinutes { get; set; } = 30;
}

/// <summary>What is wrong with the backups, judged from their journal. Pure.</summary>
public static class BackupHealth
{
    public static IReadOnlyList<string> Evaluate(IReadOnlyList<BackupRunDto> latestPerKind, DateTimeOffset? lastGoodDump, DateTimeOffset now, TimeSpan uptime, TimeSpan staleAfter)
    {
        var problems = new List<string>();
        var dump = latestPerKind.FirstOrDefault(r => r.Kind == "dump");
        var offsite = latestPerKind.FirstOrDefault(r => r.Kind == "offsite");
        var verify = latestPerKind.FirstOrDefault(r => r.Kind == "verify");

        if (dump is { Ok: false })
            problems.Add("dump_failed");
        // After an install nothing has run yet: a missing dump is a problem only once the hive has been up as long as the limit.
        if (lastGoodDump is { } good ? now - good > staleAfter : uptime > staleAfter)
            problems.Add("dump_stale");
        if (offsite is { Ok: false })
            problems.Add("offsite_failed");
        if (verify is { Ok: false })
            problems.Add("verify_failed");
        return problems;
    }
}

public sealed class BackupService(HiveDbContext db, IOptions<BackupOptions> options, TimeProvider time)
{
    private static BackupRunDto ToDto(BackupRun r) => new(r.Id, r.Kind, r.StartedAt, r.FinishedAt, r.Ok, r.Bytes, r.Detail);

    public async Task<BackupStatusDto> GetAsync(TimeSpan uptime, CancellationToken ct)
    {
        var latest = new List<BackupRunDto>();
        foreach (var kind in new[] { "dump", "offsite", "verify" })
            if (await db.BackupRuns.AsNoTracking().Where(r => r.Kind == kind).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct) is { } run)
                latest.Add(ToDto(run));
        var lastGood = await db.BackupRuns.AsNoTracking().Where(r => r.Kind == "dump" && r.Ok)
            .OrderByDescending(r => r.FinishedAt).Select(r => (DateTimeOffset?)r.FinishedAt).FirstOrDefaultAsync(ct);
        var recent = (await db.BackupRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(20).ToListAsync(ct)).Select(ToDto).ToList();
        var problems = BackupHealth.Evaluate(latest, lastGood, time.GetUtcNow(), uptime, TimeSpan.FromHours(options.Value.StaleAfterHours));
        return new BackupStatusDto(problems.Count == 0, problems, lastGood,
            latest.FirstOrDefault(r => r.Kind == "dump"), latest.FirstOrDefault(r => r.Kind == "offsite"), latest.FirstOrDefault(r => r.Kind == "verify"), recent);
    }
}

/// <summary>
/// Tells the family when the backups go wrong (a warn event, so Telegram gets it) and when they are well again.
/// What it last said is kept in the modes table, so a restart does not repeat it.
/// </summary>
public sealed class BackupWatch(IServiceScopeFactory scopes, IngestQueue ingest, IOptions<BackupOptions> options, IOptions<HiveOptions> hive, TimeProvider time, ILogger<BackupWatch> logger) : BackgroundService
{
    internal const string StateKey = "backup.alert";
    private readonly DateTimeOffset _started = time.GetUtcNow();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The build runs the app to write the OpenAPI document; it must not need a database.
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.CheckEveryMinutes)), time);
        try
        {
            do
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Backup check failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
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
        var status = await scope.ServiceProvider.GetRequiredService<BackupService>().GetAsync(now - _started, ct);
        var current = string.Join(',', status.Problems.Order());

        var saved = await db.Modes.FirstOrDefaultAsync(m => m.Key == StateKey, ct);
        var before = saved?.Value ?? "";
        if (before == current)
            return;

        if (saved is null)
            db.Modes.Add(new Mode { Key = StateKey, Value = current, UpdatedAt = now, UpdatedBy = "system:backup" });
        else
        {
            saved.Value = current;
            saved.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);

        var deviceId = $"hive-{hive.Value.Node}";
        if (current.Length > 0)
        {
            var details = status.Recent.Where(r => !r.Ok).Select(r => new { r.Kind, r.Detail }).Take(3).ToList();
            ingest.Enqueue(new DeviceEventReceived(deviceId, Ulid.New(now), now, now, "backup", EventSeverity.Warn, null,
                JsonSerializer.SerializeToDocument(new { problems = status.Problems, details, lastGoodDumpAt = status.LastGoodDumpAt })));
        }
        else if (before.Length > 0)
        {
            ingest.Enqueue(new DeviceEventReceived(deviceId, Ulid.New(now), now, now, "backup_cleared", EventSeverity.Info, null,
                JsonSerializer.SerializeToDocument(new { lastGoodDumpAt = status.LastGoodDumpAt })));
        }
    }
}

public static class BackupModule
{
    public const string Name = "backup";

    public static IServiceCollection AddBackupModule(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.Configure<BackupOptions>(configuration.GetSection(BackupOptions.Section));
        services.AddScoped<BackupService>();
        services.AddHostedService<BackupWatch>();
        return services;
    }

    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/backups", (BackupService s, CancellationToken ct) =>
                s.GetAsync(DateTimeOffset.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(), ct))
            .WithTags("backups")
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }
}
