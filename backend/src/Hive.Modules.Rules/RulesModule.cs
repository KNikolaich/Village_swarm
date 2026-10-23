using System.Security.Claims;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Hive.Modules.Telemetry.Ingest;
using Hive.Modules.Weather;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Rules;

/// <summary>Rules: stored definitions, the journal, and the dry run over past hours (spec 6.6).</summary>
public sealed class RuleService(HiveDbContext db, RuleStore store, RuleDataLoader loader, WeatherService weather, IOptions<HiveOptions> hive, TimeProvider time)
{
    public const int MaxDryRunHours = 168;
    private const int MaxHits = 500;

    public async Task<IReadOnlyList<RuleDto>> ListAsync(CancellationToken ct) =>
        (await db.Rules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<RuleDto?> GetAsync(int id, CancellationToken ct) =>
        await db.Rules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) is { } r ? ToDto(r) : null;

    public async Task<RuleDto> CreateAsync(SaveRuleRequest request, string by, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var row = new Rule { Name = request.Name.Trim(), Enabled = request.Enabled, Definition = RuleStore.Write(request.Definition), UpdatedAt = now, UpdatedBy = by };
        db.Rules.Add(row);
        Audit.Record(db, now, by, "rule_create", row.Name);
        await db.SaveChangesAsync(ct);
        store.Invalidate();
        return ToDto(row);
    }

    public async Task<RuleDto?> UpdateAsync(int id, SaveRuleRequest request, string by, CancellationToken ct)
    {
        var row = await db.Rules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
            return null;
        var now = time.GetUtcNow();
        row.Name = request.Name.Trim();
        row.Enabled = request.Enabled;
        row.Definition = RuleStore.Write(request.Definition);
        row.State = null; // new conditions start without old hysteresis latches
        row.LastOutcome = null;
        row.UpdatedAt = now;
        row.UpdatedBy = by;
        Audit.Record(db, now, by, request.Enabled ? "rule_update" : "rule_disable", row.Name);
        await db.SaveChangesAsync(ct);
        store.Invalidate();
        return ToDto(row);
    }

    /// <summary>The "Авто" switch of a relay banner: the rule as it is, only switched on or off.</summary>
    public async Task<RuleDto?> SetEnabledAsync(int id, bool enabled, string by, CancellationToken ct)
    {
        var row = await db.Rules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
            return null;
        if (row.Enabled != enabled)
        {
            var now = time.GetUtcNow();
            row.Enabled = enabled;
            row.UpdatedAt = now;
            row.UpdatedBy = by;
            Audit.Record(db, now, by, enabled ? "rule_enable" : "rule_disable", row.Name);
            await db.SaveChangesAsync(ct);
            store.Invalidate();
        }
        return ToDto(row);
    }

    public async Task<bool> DeleteAsync(int id, string by, CancellationToken ct)
    {
        var row = await db.Rules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
            return false;
        db.Rules.Remove(row);
        Audit.Record(db, time.GetUtcNow(), by, "rule_delete", row.Name);
        await db.SaveChangesAsync(ct);
        store.Invalidate();
        return true;
    }

    public async Task<IReadOnlyList<RuleRunDto>> RunsAsync(int id, int? limit, CancellationToken ct) =>
        (await db.RuleRuns.AsNoTracking().Where(r => r.RuleId == id).OrderByDescending(r => r.Ts).ThenByDescending(r => r.Id)
            .Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct))
        .Select(r => new RuleRunDto(r.Id, r.Ts, r.Outcome, r.Details?.RootElement.Clone())).ToList();

    /// <summary>
    /// Replays the rule over the past <paramref name="hours"/>: every moment its trigger would have come, the conditions
    /// are judged on the data of that moment, cooldown included. Nothing is sent.
    /// </summary>
    public async Task<DryRunResult> DryRunAsync(RuleDefinition rule, int? hours, CancellationToken ct)
    {
        var to = time.GetUtcNow();
        var from = to.AddHours(-Math.Clamp(hours ?? 24, 1, MaxDryRunHours));
        var zone = hive.Value.Zone;
        var data = await loader.LoadAsync(rule, from, to, ct);
        // Notes are codes the UI translates: current_state, offline_not_replayed, truncated.
        var notes = new List<string> { "current_state" };

        IEnumerable<DateTimeOffset> moments;
        var t = rule.Trigger;
        switch (t.Kind)
        {
            case "telemetry":
                moments = data.Series.TryGetValue((t.Device!, t.Metric!), out var points) ? points.Where(p => p.Ts > from).Select(p => p.Ts).ToList() : [];
                break;
            case "event":
                moments = await db.Events.AsNoTracking()
                    .Where(e => e.Type == t.EventType && (t.Device == null || e.DeviceId == t.Device) && e.Ts > from && e.Ts <= to)
                    .OrderBy(e => e.Ts).Select(e => e.Ts).ToListAsync(ct);
                break;
            case "schedule":
                moments = RuleSchedule.Scheduled(t, from, to, zone).ToList();
                break;
            case "sun":
                moments = RuleSchedule.SunTimes(t, (await weather.GetAsync(ct)).Daily).Where(s => s > from && s <= to).ToList();
                break;
            default:
                moments = [];
                notes.Add("offline_not_replayed");
                break;
        }

        var latches = new Dictionary<int, bool>();
        var hits = new List<DryRunHit>();
        DateTimeOffset? lastFired = null;
        var lastWasNoData = false;
        var checkedCount = 0;
        foreach (var at in moments)
        {
            checkedCount++;
            if (lastFired is { } lf && at - lf < TimeSpan.FromSeconds(rule.CooldownS))
                continue;
            var verdict = RuleEvaluator.Evaluate(rule, data, at, zone, latches);
            if (verdict.Outcome == EvalOutcome.Pass)
            {
                hits.Add(new DryRunHit(at, "fired", verdict.TriggerValue, null));
                lastFired = at;
            }
            else if (verdict.Outcome == EvalOutcome.NoData && !lastWasNoData)
                hits.Add(new DryRunHit(at, "no_data", verdict.TriggerValue, verdict.Reason));
            lastWasNoData = verdict.Outcome == EvalOutcome.NoData;
            if (hits.Count >= MaxHits)
            {
                notes.Add("truncated");
                break;
            }
        }
        return new DryRunResult(from, to, checkedCount, hits, notes);
    }

    private static RuleDto ToDto(Rule r) => new(r.Id, r.Name, r.Enabled, RuleStore.Read(r.Definition), r.LastFiredAt, r.LastOutcome, r.UpdatedAt);
}

public static class RulesModule
{
    public const string Name = "rules";

    public static IServiceCollection AddRulesModule(this IServiceCollection services)
    {
        services.AddSingleton<RuleStore>();
        services.AddScoped<RuleDataLoader>();
        services.AddScoped<RuleRunner>();
        services.AddScoped<RuleService>();
        services.AddSingleton<IIngestListener, RuleEngine>();
        services.AddHostedService<RuleScheduler>();
        return services;
    }

    private static string? Check(SaveRuleRequest r) =>
        string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 128 ? "A name of 1..128 characters is required" : RuleEvaluator.Validate(r.Definition);

    private static ValidationProblem Problem(string message) => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["rule"] = [message] });

    public static IEndpointRouteBuilder MapRulesEndpoints(this IEndpointRouteBuilder app)
    {
        var rules = app.MapGroup("/api/rules").WithTags("rules");
        rules.MapGet("", (RuleService s, CancellationToken ct) => s.ListAsync(ct));
        rules.MapGet("/{id:int}", async Task<Results<Ok<RuleDto>, NotFound>> (int id, RuleService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } r ? TypedResults.Ok(r) : TypedResults.NotFound());
        rules.MapGet("/{id:int}/runs", (int id, int? limit, RuleService s, CancellationToken ct) => s.RunsAsync(id, limit, ct));

        rules.MapPost("", async Task<Results<Created<RuleDto>, ValidationProblem>> (SaveRuleRequest request, ClaimsPrincipal user, RuleService s, CancellationToken ct) =>
            {
                if (Check(request) is { } problem)
                    return Problem(problem);
                var created = await s.CreateAsync(request, $"user:{user.Identity?.Name}", ct);
                return TypedResults.Created($"/api/rules/{created.Id}", created);
            })
            .RequireAuthorization(HiveRoles.AdminOnly);

        rules.MapPut("/{id:int}", async Task<Results<Ok<RuleDto>, NotFound, ValidationProblem>> (int id, SaveRuleRequest request, ClaimsPrincipal user, RuleService s, CancellationToken ct) =>
            {
                if (Check(request) is { } problem)
                    return Problem(problem);
                return await s.UpdateAsync(id, request, $"user:{user.Identity?.Name}", ct) is { } r ? TypedResults.Ok(r) : TypedResults.NotFound();
            })
            .RequireAuthorization(HiveRoles.AdminOnly);

        // Switching automation on and off is for the family too (like a relay); writing rules stays the administrator's.
        rules.MapPut("/{id:int}/enabled", async Task<Results<Ok<RuleDto>, NotFound>> (int id, SetRuleEnabledRequest request, ClaimsPrincipal user, RuleService s, CancellationToken ct) =>
                await s.SetEnabledAsync(id, request.Enabled, $"user:{user.Identity?.Name}", ct) is { } r ? TypedResults.Ok(r) : TypedResults.NotFound())
            .RequireAuthorization(HiveRoles.CanControl);

        rules.MapDelete("/{id:int}", async Task<Results<NoContent, NotFound>> (int id, ClaimsPrincipal user, RuleService s, CancellationToken ct) =>
                await s.DeleteAsync(id, $"user:{user.Identity?.Name}", ct) ? TypedResults.NoContent() : TypedResults.NotFound())
            .RequireAuthorization(HiveRoles.AdminOnly);

        rules.MapPost("/dry-run", async Task<Results<Ok<DryRunResult>, ValidationProblem>> (DryRunRequest request, RuleService s, CancellationToken ct) =>
            {
                if (RuleEvaluator.Validate(request.Definition) is { } problem)
                    return Problem(problem);
                return TypedResults.Ok(await s.DryRunAsync(request.Definition, request.Hours, ct));
            })
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }
}
