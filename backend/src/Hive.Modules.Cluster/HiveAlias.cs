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

/// <summary>
/// The web app manifest of this node: the installed app and the browser name themselves after the hive ("Улей — Дача"), so two hives
/// on one phone are told apart. Same icons and colors as the static one the front builds (frontend/vite.config.ts); this one wins in
/// Caddy and the static file stays as the fallback when the api is down.
/// </summary>
public static class HiveManifest
{
    public static object Build(string? alias)
    {
        var name = string.IsNullOrWhiteSpace(alias) ? "Улей" : alias.Trim();
        return new
        {
            name = name == "Улей" ? name : $"Улей — {name}",
            short_name = name,
            description = "Охрана, климат и фото загородного дома",
            lang = "ru",
            theme_color = "#0a0a0a",
            background_color = "#0a0a0a",
            display = "standalone",
            start_url = "/",
            scope = "/",
            icons = new object[]
            {
                new { src = "pwa-192.png", sizes = "192x192", type = "image/png" },
                new { src = "pwa-512.png", sizes = "512x512", type = "image/png" },
                new { src = "pwa-maskable-512.png", sizes = "512x512", type = "image/png", purpose = "maskable" },
            },
        };
    }
}

public static class HiveAliasEndpoints
{
    public static IEndpointRouteBuilder MapAliasEndpoints(this IEndpointRouteBuilder app)
    {
        // The browser asks for it without signing in, and it must not outlive a rename (the alias is the app's name).
        app.MapGet("/api/manifest.webmanifest", async (HiveAliasService s, HttpResponse response, CancellationToken ct) =>
            {
                response.Headers.CacheControl = "no-store";
                return Results.Json(HiveManifest.Build(await s.GetAsync(ct)), contentType: "application/manifest+json");
            })
            .AllowAnonymous()
            .ExcludeFromDescription();
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
