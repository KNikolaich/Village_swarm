using System.Security.Cryptography;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Users;

public sealed record TrustedDeviceDto(int Id, string? Name, string? LastIp, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, bool Current);

/// <summary>Browsers that passed the second factor (hive_device cookie).</summary>
public sealed class TrustedDeviceService(HiveDbContext db, TimeProvider time, IOptions<AuthOptions> options)
{
    public const string CookieName = "hive_device";

    public static string? CookieHash(HttpContext http) =>
        http.Request.Cookies.TryGetValue(CookieName, out var token) && token.Length is > 20 and < 200 ? LoginThrottleService.Sha256(token) : null;

    /// <summary>True when this browser is trusted for the user; refreshes its "last seen".</summary>
    public async Task<bool> TouchAsync(HiveUser user, HttpContext http, CancellationToken ct = default)
    {
        if (CookieHash(http) is not { } hash)
            return false;
        var now = time.GetUtcNow();
        var device = await db.TrustedDevices.FirstOrDefaultAsync(d => d.UserId == user.Id && d.TokenHash == hash, ct);
        if (device is null)
            return false;
        if (now - device.LastSeenAt > TimeSpan.FromDays(options.Value.TrustedDeviceDays))
        {
            db.TrustedDevices.Remove(device);
            await db.SaveChangesAsync(ct);
            return false;
        }
        device.LastSeenAt = now;
        device.LastIp = LoginThrottleService.ClientIp(http);
        device.Name = UserAgents.Describe(http.Request.Headers.UserAgent);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Remembers this browser for the user; the browser keeps its token if it already has one.</summary>
    public async Task TrustAsync(HiveUser user, HttpContext http, CancellationToken ct = default)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var token) || CookieHash(http) is null)
            token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = LoginThrottleService.Sha256(token);
        var now = time.GetUtcNow();
        if (!await db.TrustedDevices.AnyAsync(d => d.UserId == user.Id && d.TokenHash == hash, ct))
        {
            db.TrustedDevices.Add(new TrustedDevice
            {
                UserId = user.Id,
                TokenHash = hash,
                Name = UserAgents.Describe(http.Request.Headers.UserAgent),
                LastIp = LoginThrottleService.ClientIp(http),
                CreatedAt = now,
                LastSeenAt = now,
            });
            await db.SaveChangesAsync(ct);
        }
        http.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/api",
            MaxAge = TimeSpan.FromDays(400), // the longest a browser keeps a cookie
            IsEssential = true,
        });
    }

    public async Task<IReadOnlyList<TrustedDeviceDto>> ListAsync(int userId, HttpContext http, CancellationToken ct = default)
    {
        var devices = await db.TrustedDevices.AsNoTracking().Where(d => d.UserId == userId).OrderByDescending(d => d.LastSeenAt).ToListAsync(ct);
        var current = CookieHash(http);
        return devices.Select(d => new TrustedDeviceDto(d.Id, d.Name, d.LastIp, d.CreatedAt, d.LastSeenAt, d.TokenHash == current)).ToList();
    }

    public async Task<bool> ForgetAsync(int userId, int id, CancellationToken ct = default) =>
        await db.TrustedDevices.Where(d => d.UserId == userId && d.Id == id).ExecuteDeleteAsync(ct) > 0;

    public Task ForgetAllAsync(int userId, CancellationToken ct = default) =>
        db.TrustedDevices.Where(d => d.UserId == userId).ExecuteDeleteAsync(ct);
}

/// <summary>"Chrome · Android" out of a User-Agent: enough to recognise one's own phone in a list.</summary>
public static class UserAgents
{
    public static string Describe(string? userAgent)
    {
        var ua = userAgent ?? "";
        string browser = ua switch
        {
            _ when ua.Contains("YaBrowser/", StringComparison.Ordinal) => "Яндекс Браузер",
            _ when ua.Contains("Edg/", StringComparison.Ordinal) || ua.Contains("EdgA/", StringComparison.Ordinal) => "Edge",
            _ when ua.Contains("OPR/", StringComparison.Ordinal) => "Opera",
            _ when ua.Contains("Firefox/", StringComparison.Ordinal) || ua.Contains("FxiOS/", StringComparison.Ordinal) => "Firefox",
            _ when ua.Contains("Chrome/", StringComparison.Ordinal) || ua.Contains("CriOS/", StringComparison.Ordinal) => "Chrome",
            _ when ua.Contains("Safari/", StringComparison.Ordinal) => "Safari",
            _ => "Браузер",
        };
        string? os = ua switch
        {
            _ when ua.Contains("Android", StringComparison.Ordinal) => "Android",
            _ when ua.Contains("iPhone", StringComparison.Ordinal) => "iPhone",
            _ when ua.Contains("iPad", StringComparison.Ordinal) => "iPad",
            _ when ua.Contains("Windows", StringComparison.Ordinal) => "Windows",
            _ when ua.Contains("Mac OS X", StringComparison.Ordinal) => "macOS",
            _ when ua.Contains("Linux", StringComparison.Ordinal) => "Linux",
            _ => null,
        };
        return os is null ? browser : $"{browser} · {os}";
    }
}
