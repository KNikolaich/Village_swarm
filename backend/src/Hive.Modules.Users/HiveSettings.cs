using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Hive.Modules.Users;

public sealed record LanguageDto(string Language);

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
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }
}
