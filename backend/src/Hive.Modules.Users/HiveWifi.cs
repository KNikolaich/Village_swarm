using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Hive.Modules.Users;

/// <summary>What a board needs to join the hive, kept once for the whole hive. Shown to admins only.</summary>
public sealed record WifiSettingsDto(string? Ssid, bool PasswordSet, string? HiveUrl);

public sealed record WifiSettingsRequest(string Ssid, string? Password, string HiveUrl);

/// <summary>
/// The Wi-Fi network and the hive address for new and re-configured hornets, so nobody types a long password per board.
/// The password is encrypted at rest (ASP.NET data protection, keys in /srv/hive/keys) and never returned by the API
/// except to the setup wizard, which needs it to send it to the board over USB.
/// </summary>
public sealed class HiveWifiService(HiveDbContext db, IDataProtectionProvider protection, TimeProvider time)
{
    public const string SsidKey = "wifi.ssid";
    public const string PasswordKey = "wifi.password";
    public const string HiveUrlKey = "hive.url";
    private readonly IDataProtector _protector = protection.CreateProtector("hive.wifi");

    public async Task<WifiSettingsDto> GetAsync(CancellationToken ct)
    {
        var rows = await db.Modes.AsNoTracking().Where(m => m.Key == SsidKey || m.Key == PasswordKey || m.Key == HiveUrlKey).ToListAsync(ct);
        return new WifiSettingsDto(
            rows.FirstOrDefault(r => r.Key == SsidKey)?.Value,
            rows.Any(r => r.Key == PasswordKey),
            rows.FirstOrDefault(r => r.Key == HiveUrlKey)?.Value);
    }

    /// <summary>The values the setup wizard sends to a board. Only for admins.</summary>
    public async Task<WifiSecrets?> RevealAsync(CancellationToken ct)
    {
        var s = await GetAsync(ct);
        var password = await db.Modes.AsNoTracking().Where(m => m.Key == PasswordKey).Select(m => m.Value).FirstOrDefaultAsync(ct);
        if (s.Ssid is null || password is null)
            return null;
        return new WifiSecrets(s.Ssid, _protector.Unprotect(password), s.HiveUrl);
    }

    public async Task SaveAsync(WifiSettingsRequest r, string by, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await Upsert(SsidKey, r.Ssid.Trim(), now, by, ct);
        await Upsert(HiveUrlKey, r.HiveUrl.Trim().TrimEnd('/'), now, by, ct);
        // An empty password keeps the stored one (the form does not show it back).
        if (!string.IsNullOrEmpty(r.Password))
            await Upsert(PasswordKey, _protector.Protect(r.Password), now, by, ct);
        Audit.Record(db, now, by, "set_wifi", null, new { ssid = r.Ssid.Trim(), passwordChanged = !string.IsNullOrEmpty(r.Password) });
        await db.SaveChangesAsync(ct);
    }

    private async Task Upsert(string key, string value, DateTimeOffset now, string by, CancellationToken ct)
    {
        var row = await db.Modes.FirstOrDefaultAsync(m => m.Key == key, ct);
        if (row is null)
            db.Modes.Add(new Mode { Key = key, Value = value, UpdatedAt = now, UpdatedBy = by });
        else
        {
            row.Value = value;
            row.UpdatedAt = now;
            row.UpdatedBy = by;
        }
    }
}

public sealed record WifiSecrets(string Ssid, string Password, string? HiveUrl);

public static class HiveWifiEndpoints
{
    public static IEndpointRouteBuilder MapWifiEndpoints(this IEndpointRouteBuilder app)
    {
        var wifi = app.MapGroup("/api/settings/wifi").WithTags("settings").RequireAuthorization(HiveRoles.AdminOnly);
        wifi.MapGet("", async (HiveWifiService s, CancellationToken ct) => TypedResults.Ok(await s.GetAsync(ct)));
        wifi.MapPut("", async Task<Results<NoContent, ValidationProblem>> (WifiSettingsRequest request, System.Security.Claims.ClaimsPrincipal user, HiveWifiService s, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.Ssid) || request.Ssid.Length > 32)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["ssid"] = ["1–32 символа"] });
                if (!string.IsNullOrEmpty(request.Password) && request.Password.Length is < 8 or > 63)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["password"] = ["Wi-Fi password: 8–63 символа"] });
                if (!Uri.TryCreate(request.HiveUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["hiveUrl"] = ["адрес вида http://192.168.10.4"] });
                await s.SaveAsync(request, $"user:{user.Identity?.Name}", ct);
                return TypedResults.NoContent();
            });
        // Only the setup wizard calls this, to send the values to a board over USB.
        wifi.MapGet("/reveal", async Task<Results<Ok<WifiSecrets>, NotFound>> (HiveWifiService s, CancellationToken ct) =>
            await s.RevealAsync(ct) is { } secrets ? TypedResults.Ok(secrets) : TypedResults.NotFound());
        return app;
    }
}
