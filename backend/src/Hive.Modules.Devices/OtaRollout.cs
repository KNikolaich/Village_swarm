using System.Text.Json;
using Hive.Contracts;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Devices;

/// <summary>The hornets to update, named one by one; ESP8266 boards are only taken with <see cref="ConfirmNoRollback"/>.</summary>
public sealed record OtaRolloutRequest(IReadOnlyList<string> DeviceIds, bool ConfirmNoRollback = false);

public enum OtaRolloutState
{
    Running,
    /// <summary>Stop was pressed: no new hornet is started, the one updating now is still watched.</summary>
    Stopping,
    Done,
    Stopped,
    Failed,
}

public enum OtaRolloutItemState
{
    Pending,
    Updating,
    Done,
    Failed,
    Skipped,
}

/// <summary>
/// One hornet of a rollout. Error says why it failed or was skipped: offline, asleep, up_to_date, needs_confirmation,
/// stopped, timeout, or the hornet's / hive's own words.
/// </summary>
public sealed record OtaRolloutItemDto(
    string DeviceId, string Name, string Build, string? FromVersion, string ToVersion, bool NoRollback,
    OtaRolloutItemState State, string? Error, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

public sealed record OtaRolloutDto(
    OtaRolloutState State, string StartedBy, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, IReadOnlyList<OtaRolloutItemDto> Items);

/// <summary>The rollout running now, or the last one since the hive started (null: none).</summary>
public sealed record OtaRolloutStatusDto(OtaRolloutDto? Rollout);

/// <summary>The rules of a rollout, kept apart from the runner so they can be tested on their own.</summary>
public static class OtaRolloutRules
{
    /// <summary>
    /// How long one hornet may take: download, flash, restart and report the new version. The ota command lives as
    /// long, so a hornet that wakes up later does not start on an order the hive has given up on.
    /// </summary>
    public static readonly TimeSpan ItemTimeout = TimeSpan.FromMinutes(10);

    /// <summary>An ESP8266 copies the new image over the old one at restart and has nothing to go back to.</summary>
    public static bool NoRollback(string chipFamily) => chipFamily.StartsWith("ESP8266", StringComparison.OrdinalIgnoreCase);

    /// <summary>The newest build for this role and board whose version differs from the running one (null: up to date).</summary>
    public static T? NewerBuild<T>(IEnumerable<T> builds, Func<T, (string Role, string Board, string? Version, DateTime Built)> describe,
        string type, string? hw, string? fw) where T : class =>
        builds.Select(b => (Build: b, D: describe(b)))
            .Where(x => x.D.Role == type && x.D.Board == hw && x.D.Version is not null && x.D.Version != fw)
            .OrderByDescending(x => x.D.Built)
            .Select(x => x.Build)
            .FirstOrDefault();

    /// <summary>Boards that can roll back go first, so a bad build shows itself before it reaches one that cannot; then by name.</summary>
    public static IEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, bool> noRollback, Func<T, string> name) =>
        items.OrderBy(noRollback).ThenBy(name, StringComparer.CurrentCulture);

    public enum Verdict
    {
        Waiting,
        Done,
        Failed,
    }

    /// <summary>
    /// How the hornet being updated is doing. Failed: it said so (event ota failed or rolled_back), refused the command,
    /// or did not come back with the new version in <see cref="ItemTimeout"/>. Done: it runs the new version and is online.
    /// </summary>
    public static (Verdict Verdict, string? Error) Judge(string target, string? fw, DeviceStatus status, DateTimeOffset sentAt, DateTimeOffset now,
        CommandStatus? command, string? commandError, string? otaPhase, string? otaError)
    {
        if (otaPhase is "failed" or "rolled_back")
            return (Verdict.Failed, otaError is { Length: > 0 } ? $"{otaPhase}: {otaError}" : otaPhase);
        if (fw == target && status == DeviceStatus.Online)
            return (Verdict.Done, null);
        if (command is CommandStatus.Rejected or CommandStatus.Error or CommandStatus.Unsupported or CommandStatus.Expired)
            return (Verdict.Failed, commandError is { Length: > 0 } ? commandError : $"command {command.ToString()!.ToLowerInvariant()}");
        if (now - sentAt > ItemTimeout)
            return (Verdict.Failed, "timeout");
        return (Verdict.Waiting, null);
    }
}

/// <summary>
/// "Update all outdated" (Swarm screen): the chosen hornets get their newer build one at a time, each only after the
/// previous one came back with its new version. The first failure stops the rest, so a bad build cannot reach every
/// board; the result goes to Events (and Telegram when it failed). The rollout lives in memory: a hive restart ends it,
/// and the hornet that was updating finishes on its own.
/// </summary>
public sealed class OtaRolloutRunner(IServiceScopeFactory scopes, IngestQueue ingest, IOptions<HiveOptions> hive, IOptions<ProvisioningOptions> provisioning,
    IHostEnvironment env, TimeProvider time, ILogger<OtaRolloutRunner> logger) : BackgroundService
{
    private sealed class Item
    {
        public required string DeviceId { get; init; }
        public required string Name { get; init; }
        public required string Build { get; init; }
        public string? FromVersion { get; init; }
        public required string ToVersion { get; init; }
        public bool NoRollback { get; init; }
        public OtaRolloutItemState State { get; set; }
        public string? Error { get; set; }
        public string? CommandId { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
    }

    private sealed class Rollout
    {
        public OtaRolloutState State { get; set; } = OtaRolloutState.Running;
        public required string StartedBy { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; set; }
        public required List<Item> Items { get; init; }
        public bool Active => State is OtaRolloutState.Running or OtaRolloutState.Stopping;
    }

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _tick = new(1, 1);
    private Rollout? _rollout;

    public OtaRolloutStatusDto Status()
    {
        lock (_gate)
            return new OtaRolloutStatusDto(_rollout is { } r
                ? new OtaRolloutDto(r.State, r.StartedBy, r.StartedAt, r.FinishedAt, r.Items.Select(i => new OtaRolloutItemDto(
                    i.DeviceId, i.Name, i.Build, i.FromVersion, i.ToVersion, i.NoRollback, i.State, i.Error, i.StartedAt, i.FinishedAt)).ToList())
                : null);
    }

    /// <summary>Plans the rollout and starts the first hornet. Error: busy (one is running) or nothing (no hornet to update).</summary>
    public async Task<(OtaRolloutStatusDto? Status, string? Error)> StartAsync(OtaRolloutRequest request, string by, CancellationToken ct)
    {
        lock (_gate)
            if (_rollout is { Active: true })
                return (null, "busy");

        var now = time.GetUtcNow();
        var builds = ProvisioningEndpoints.ListBuilds(provisioning.Value, env);
        var ids = request.DeviceIds.ToHashSet(StringComparer.Ordinal);
        List<Item> items;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
            var devices = await db.Devices.AsNoTracking()
                .Where(d => ids.Contains(d.DeviceId) && d.Status != DeviceStatus.Disabled && d.Driver == Drivers.Hornet && d.TypeCode != "hive")
                .ToListAsync(ct);
            items = [];
            foreach (var d in devices)
            {
                if (OtaRolloutRules.NewerBuild(builds, b => (b.Role, b.Board, b.Version, b.Built), d.TypeCode, d.Hw, d.FwVersion) is not { } build)
                    continue; // up to date by now: nothing to show for it
                var noRollback = OtaRolloutRules.NoRollback(build.ChipFamily);
                var confirmed = !noRollback || request.ConfirmNoRollback;
                items.Add(new Item
                {
                    DeviceId = d.DeviceId, Name = d.Name, Build = build.Name, FromVersion = d.FwVersion, ToVersion = build.Version!, NoRollback = noRollback,
                    State = confirmed ? OtaRolloutItemState.Pending : OtaRolloutItemState.Skipped,
                    Error = confirmed ? null : "needs_confirmation",
                    FinishedAt = confirmed ? null : now,
                });
            }
            if (!items.Any(i => i.State == OtaRolloutItemState.Pending))
                return (null, "nothing");
            Audit.Record(db, now, by, "ota_rollout", null, new
            {
                hornets = items.Where(i => i.State == OtaRolloutItemState.Pending).Select(i => new { i.DeviceId, i.Build, i.ToVersion }),
            });
            await db.SaveChangesAsync(ct);
        }

        lock (_gate)
        {
            if (_rollout is { Active: true })
                return (null, "busy");
            _rollout = new Rollout
            {
                StartedBy = by,
                StartedAt = now,
                Items = OtaRolloutRules.Order(items, i => i.NoRollback, i => i.Name).ToList(),
            };
        }
        logger.LogInformation("OTA rollout started by {By}: {Count} hornets", by, items.Count(i => i.State == OtaRolloutItemState.Pending));
        await TickAsync(ct);
        return (Status(), null);
    }

    /// <summary>No new hornet is started; the one updating now is still watched to the end. False: nothing is running.</summary>
    public async Task<bool> StopAsync(string by, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_rollout is not { State: OtaRolloutState.Running } r)
                return false;
            r.State = OtaRolloutState.Stopping;
        }
        logger.LogInformation("OTA rollout stopped by {By}", by);
        await TickAsync(ct);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "OTA rollout step failed");
            }
        }
    }

    /// <summary>One step: judge the hornet that is updating, then start the next one (or finish).</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        await _tick.WaitAsync(ct);
        try
        {
            Rollout? r;
            lock (_gate)
                r = _rollout is { Active: true } ? _rollout : null;
            if (r is null)
                return;

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
            var now = time.GetUtcNow();

            if (r.Items.FirstOrDefault(i => i.State == OtaRolloutItemState.Updating) is { } current)
            {
                var (verdict, error) = await JudgeAsync(db, current, now, ct);
                if (verdict == OtaRolloutRules.Verdict.Waiting)
                    return;
                lock (_gate)
                {
                    current.State = verdict == OtaRolloutRules.Verdict.Done ? OtaRolloutItemState.Done : OtaRolloutItemState.Failed;
                    current.Error = error;
                    current.FinishedAt = now;
                }
                if (verdict == OtaRolloutRules.Verdict.Failed)
                {
                    Finish(r, OtaRolloutState.Failed, now);
                    return;
                }
            }

            if (r.State == OtaRolloutState.Stopping)
            {
                Finish(r, OtaRolloutState.Stopped, now);
                return;
            }

            var ota = scope.ServiceProvider.GetRequiredService<OtaService>();
            foreach (var next in r.Items.Where(i => i.State == OtaRolloutItemState.Pending).ToList())
            {
                var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == next.DeviceId, ct);
                string? skip = device is null || device.Status != DeviceStatus.Online ? "offline"
                    : DeviceConfigService.Asleep(device, now) ? "asleep"
                    : device.FwVersion == next.ToVersion ? "up_to_date"
                    : null;
                if (skip is null)
                {
                    var result = await ota.StartAsync(next.DeviceId, next.Build, r.StartedBy, ct);
                    if (result.Status == OtaStatus.Sent)
                    {
                        lock (_gate)
                        {
                            next.State = OtaRolloutItemState.Updating;
                            next.CommandId = result.Command!.Cid;
                            next.StartedAt = now;
                        }
                        return;
                    }
                    if (result.Status != OtaStatus.UnknownDevice)
                    {
                        // The hive itself refused (no hive address, build gone, image too big, broker down): the
                        // next hornets would hit the same wall, so this is a failure to look at, not a skip.
                        lock (_gate)
                        {
                            next.State = OtaRolloutItemState.Failed;
                            next.Error = result.Error ?? result.Status.ToString();
                            next.FinishedAt = now;
                        }
                        Finish(r, OtaRolloutState.Failed, now);
                        return;
                    }
                    skip = "offline";
                }
                lock (_gate)
                {
                    next.State = OtaRolloutItemState.Skipped;
                    next.Error = skip;
                    next.FinishedAt = now;
                }
            }
            Finish(r, OtaRolloutState.Done, now);
        }
        finally
        {
            _tick.Release();
        }
    }

    private static async Task<(OtaRolloutRules.Verdict, string?)> JudgeAsync(HiveDbContext db, Item item, DateTimeOffset now, CancellationToken ct)
    {
        var sentAt = item.StartedAt!.Value;
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == item.DeviceId, ct);
        var command = await db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.Cid == item.CommandId, ct);
        var commandError = command?.AckPayload?.RootElement.TryGetProperty("error", out var e) == true ? e.GetString() : null;
        // The hornet's own account (contracts event-ota), newest first; only what came after this order counts.
        var events = await db.Events.AsNoTracking()
            .Where(x => x.DeviceId == item.DeviceId && x.Type == "ota" && x.ReceivedAt >= sentAt)
            .OrderByDescending(x => x.ReceivedAt).Take(5).ToListAsync(ct);
        var bad = events.Select(x => x.Payload.RootElement)
            .Where(p => p.TryGetProperty("phase", out var ph) && ph.GetString() is "failed" or "rolled_back")
            .Select(p => (Phase: p.GetProperty("phase").GetString(), Error: p.TryGetProperty("error", out var er) ? er.GetString() : null))
            .FirstOrDefault();
        return OtaRolloutRules.Judge(item.ToVersion, device?.FwVersion, device?.Status ?? DeviceStatus.Offline, sentAt, now,
            command?.Status, commandError, bad.Phase, bad.Error);
    }

    /// <summary>Ends the rollout: the rest is skipped, and the outcome goes to Events (a failure as a warning, so Telegram hears of it).</summary>
    private void Finish(Rollout r, OtaRolloutState state, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var i in r.Items.Where(i => i.State == OtaRolloutItemState.Pending))
            {
                i.State = OtaRolloutItemState.Skipped;
                i.Error = "stopped";
                i.FinishedAt = now;
            }
            r.State = state;
            r.FinishedAt = now;
        }
        var failed = r.Items.FirstOrDefault(i => i.State == OtaRolloutItemState.Failed);
        var payload = JsonSerializer.SerializeToDocument(new
        {
            result = state.ToString().ToLowerInvariant(),
            done = r.Items.Count(i => i.State == OtaRolloutItemState.Done),
            total = r.Items.Count,
            device = failed?.Name,
            version = failed?.ToVersion,
            error = failed?.Error,
        });
        ingest.Enqueue(new DeviceEventReceived($"hive-{hive.Value.Node}", Ulid.New(now), now, now, "ota_rollout",
            state == OtaRolloutState.Failed ? EventSeverity.Warn : EventSeverity.Info, null, payload));
        logger.LogInformation("OTA rollout {State}: {Done}/{Total} updated", state, r.Items.Count(i => i.State == OtaRolloutItemState.Done), r.Items.Count);
    }
}

public static class OtaRolloutEndpoints
{
    public static IEndpointRouteBuilder MapOtaRolloutEndpoints(this IEndpointRouteBuilder app)
    {
        var rollout = app.MapGroup("/api/firmware/rollout").WithTags("firmware").RequireAuthorization(HiveRoles.AdminOnly);
        rollout.MapGet("", (OtaRolloutRunner runner) => TypedResults.Ok(runner.Status()));
        rollout.MapPost("", async Task<Results<Ok<OtaRolloutStatusDto>, ValidationProblem, Conflict<string>>> (
            OtaRolloutRequest request, System.Security.Claims.ClaimsPrincipal user, OtaRolloutRunner runner, CancellationToken ct) =>
        {
            if (request.DeviceIds is not { Count: > 0 })
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["deviceIds"] = ["Choose the hornets to update"] });
            var (status, error) = await runner.StartAsync(request, $"user:{user.Identity?.Name}", ct);
            return error switch
            {
                null => TypedResults.Ok(status!),
                "busy" => TypedResults.Conflict("an update rollout is already running"),
                _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["deviceIds"] = ["None of these hornets has a newer build"] }),
            };
        });
        rollout.MapPost("/stop", async Task<Results<Ok<OtaRolloutStatusDto>, NotFound>> (
            System.Security.Claims.ClaimsPrincipal user, OtaRolloutRunner runner, CancellationToken ct) =>
            await runner.StopAsync($"user:{user.Identity?.Name}", ct) ? TypedResults.Ok(runner.Status()) : TypedResults.NotFound());
        return app;
    }
}
