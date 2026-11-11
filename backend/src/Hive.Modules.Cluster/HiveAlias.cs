using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Cluster;

/// <summary>The name of this hive for people: shown instead of the word "Улей" (null: no alias, the word stays).</summary>
public sealed record AliasDto(string? Alias);

/// <summary>
/// A hive's alias ("Дача", "Город", "Sbrbn | Home"): stored per node, shown in the menu header and in the swarm. When two nodes work together and
/// nothing was typed, the dacha is "Дача" and the city "Город"; alone a hive keeps the plain word.
/// </summary>
public sealed class HiveAliasService(HiveDbContext db, IOptions<ClusterOptions> cluster, IOptions<HiveOptions> hive, TimeProvider time)
{
    public const string Key = "hive.alias";
    public const int MaxLength = 40;

    public string? Default => cluster.Value.Enabled ? (hive.Value.Node == "city" ? "Город" : "Дача") : null;

    public async Task<string?> GetAsync(CancellationToken ct) =>
        await db.Modes.AsNoTracking().Where(m => m.Key == Key).Select(m => m.Value).FirstOrDefaultAsync(ct) is { Length: > 0 } stored
            ? stored
            : Default;

    /// <summary>Empty text clears the alias (the default comes back).</summary>
    public async Task SetAsync(string? alias, string by, CancellationToken ct)
    {
        var text = (alias ?? "").Trim();
        var now = time.GetUtcNow();
        var row = await db.Modes.FirstOrDefaultAsync(m => m.Key == Key, ct);
        if (row is null)
            db.Modes.Add(new Mode { Key = Key, Value = text, UpdatedAt = now, UpdatedBy = by });
        else
        {
            row.Value = text;
            row.UpdatedAt = now;
            row.UpdatedBy = by;
        }
        Audit.Record(db, now, by, "set_alias", null, new { alias = text });
        await db.SaveChangesAsync(ct);
    }
}

public static class HiveAliasEndpoints
{
    public static IEndpointRouteBuilder MapAliasEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/alias").NodeLocal().WithTags("settings");
        group.MapGet("", async (HiveAliasService s, CancellationToken ct) => TypedResults.Ok(new AliasDto(await s.GetAsync(ct))));
        group.MapPut("", async Task<Results<NoContent, ValidationProblem>> (AliasDto request, System.Security.Claims.ClaimsPrincipal user, HiveAliasService s, CancellationToken ct) =>
            {
                if ((request.Alias?.Trim().Length ?? 0) > HiveAliasService.MaxLength)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["alias"] = [$"at most {HiveAliasService.MaxLength} characters"] });
                await s.SetAsync(request.Alias, $"user:{user.Identity?.Name}", ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }
}
