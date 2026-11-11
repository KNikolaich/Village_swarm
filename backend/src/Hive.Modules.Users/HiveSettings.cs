using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hive.Modules.Users;

public sealed record LanguageDto(string Language);

/// <summary>One third-party secret: only whether it is set and where from (db | env | none | unreadable), never the value.</summary>
public sealed record SecretDto(string Key, string Group, string Source, DateTimeOffset? UpdatedAt, string? UpdatedBy);

/// <summary>The secrets page: the list, and where the decryption keys live (shown so they get backed up with the database).</summary>
public sealed record SecretsDto(List<SecretDto> Secrets, string KeysPath);

/// <summary>Write-only: an empty value clears the stored secret (the .env value, if any, is used again).</summary>
public sealed record SecretValueDto(string? Value);

/// <summary>What is running: the hive build (deploy/build-images.sh passes git describe, e.g. 8835483).</summary>
public sealed record VersionDto(string Version);

/// <summary>Settings shared by the whole hive (not per user): the interface language (spec 8.1).</summary>
public sealed class HiveSettingsService(HiveDbContext db, TimeProvider time)
{
    public const string LanguageKey = "ui.language";
    public static readonly string[] Languages = ["ru", "en"];

    public async Task<string> GetLanguageAsync(CancellationToken ct) =>
        await db.Modes.AsNoTracking().Where(m => m.Key == LanguageKey).Select(m => m.Value).FirstOrDefaultAsync(ct) is { } v && Languages.Contains(v)
            ? v
            : "ru";

    public async Task SetLanguageAsync(string language, string by, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var row = await db.Modes.FirstOrDefaultAsync(m => m.Key == LanguageKey, ct);
        if (row is null)
            db.Modes.Add(new Mode { Key = LanguageKey, Value = language, UpdatedAt = now, UpdatedBy = by });
        else
        {
            row.Value = language;
            row.UpdatedAt = now;
            row.UpdatedBy = by;
        }
        Audit.Record(db, now, by, "set_language", null, new { language });
        await db.SaveChangesAsync(ct);
    }
}

public static class HiveSettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/settings").WithTags("settings");
        // Everyone reads it (the login screen is translated too); only admins change it.
        settings.MapGet("/language", async (HiveSettingsService s, CancellationToken ct) => TypedResults.Ok(new LanguageDto(await s.GetLanguageAsync(ct))))
            .AllowAnonymous(); // the sign-in screen is translated too
        settings.MapPut("/language", async Task<Results<NoContent, ValidationProblem>> (LanguageDto request, System.Security.Claims.ClaimsPrincipal user, HiveSettingsService s, CancellationToken ct) =>
            {
                if (!HiveSettingsService.Languages.Contains(request.Language))
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["language"] = ["ru or en"] });
                await s.SetLanguageAsync(request.Language, $"user:{user.Identity?.Name}", ct);
                return TypedResults.NoContent();
            })
            .NodeLocal()
            .RequireAuthorization(HiveRoles.AdminOnly);
        MapSecrets(settings);
        settings.MapGet("/version", () => TypedResults.Ok(new VersionDto(HiveVersion())));
        // The service stops cleanly a moment after answering; the container's restart policy (unless-stopped) starts it again. For settings
        // that are read at start. Admins only; the page waits for /version to answer again.
        settings.MapPost("/restart", (IHostApplicationLifetime lifetime, ILoggerFactory logs, System.Security.Claims.ClaimsPrincipal user) =>
            {
                logs.CreateLogger("Restart").LogWarning("Restart requested by {User}", user.Identity?.Name);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    lifetime.StopApplication();
                });
                return TypedResults.Accepted((string?)null);
            })
            .NodeLocal()
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }

    private static void MapSecrets(RouteGroupBuilder settings)
    {
        // Admins only. Values go in and never come out; they are read at start, so the page offers a restart.
        var secrets = settings.MapGroup("/secrets").RequireAuthorization(HiveRoles.AdminOnly);
        secrets.MapGet("", async (SecretStore store, Microsoft.Extensions.Configuration.IConfiguration configuration, CancellationToken ct) =>
            TypedResults.Ok(new SecretsDto(
                (await store.StatusAsync(ct)).Select(x => new SecretDto(x.Key, x.Group, x.Source.ToString().ToLowerInvariant(), x.UpdatedAt, x.UpdatedBy)).ToList(),
                configuration["DataProtection:KeysPath"] ?? "")));
        secrets.MapPut("/{key}", async Task<Results<NoContent, NotFound, ValidationProblem>> (string key, SecretValueDto body, System.Security.Claims.ClaimsPrincipal user,
            SecretStore store, CancellationToken ct) =>
        {
            if (Secrets.Find(key) is not { } def)
                return TypedResults.NotFound();
            if (body.Value is { Length: > 2000 })
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["value"] = ["too long"] });
            await store.SetAsync(def, body.Value?.Trim(), $"user:{user.Identity?.Name}", ct);
            return TypedResults.NoContent();
        });
    }

    /// <summary>The InformationalVersion set at publish (HIVE_VERSION), without the "+commit" suffix the SDK may add.</summary>
    public static string HiveVersion()
    {
        var info = System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        return string.IsNullOrEmpty(info) ? "dev" : info.Split('+')[0];
    }
}
