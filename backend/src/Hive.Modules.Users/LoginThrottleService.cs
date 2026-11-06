using System.Security.Cryptography;
using System.Text;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Hive.Modules.Users;

/// <summary>
/// Pauses after wrong passwords and codes: 5 in a row wait an hour, 10 wait a day. A quiet day forgets them.
/// </summary>
public static class LoginThrottlePolicy
{
    public const int FirstLockAfter = 5;
    public const int SecondLockAfter = 10;
    public static readonly TimeSpan FirstLock = TimeSpan.FromHours(1);
    public static readonly TimeSpan SecondLock = TimeSpan.FromDays(1);
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(1);

    public static bool IsLocked(LoginThrottle? throttle, DateTimeOffset now) => throttle?.LockedUntil > now;

    /// <summary>Counts one more failure; returns the end of the pause it starts, if any.</summary>
    public static DateTimeOffset? RegisterFailure(LoginThrottle throttle, DateTimeOffset now)
    {
        if (!IsLocked(throttle, now) && now - throttle.LastFailureAt >= ForgetAfter)
            throttle.Failures = 0;
        throttle.Failures++;
        throttle.LastFailureAt = now;
        if (throttle.Failures >= SecondLockAfter)
            throttle.LockedUntil = now + SecondLock;
        else if (throttle.Failures == FirstLockAfter)
            throttle.LockedUntil = now + FirstLock;
        else
            return null;
        return throttle.LockedUntil;
    }
}

/// <summary>
/// Who is guessing: a trusted device (hive_device cookie known to the hive) is counted by itself, so a stranger
/// cannot lock the owner's phone out; everything else is counted by its IP, so dropping cookies does not help.
/// </summary>
public sealed class LoginThrottleService(HiveDbContext db, TimeProvider time)
{
    public async Task<string> KeyAsync(HttpContext http, CancellationToken ct = default)
    {
        if (TrustedDeviceService.CookieHash(http) is { } hash && await db.TrustedDevices.AnyAsync(d => d.TokenHash == hash, ct))
            return "device:" + hash;
        return "ip:" + (ClientIp(http) ?? "unknown");
    }

    public async Task<DateTimeOffset?> LockedUntilAsync(string key, CancellationToken ct = default)
    {
        var throttle = await db.LoginThrottles.AsNoTracking().FirstOrDefaultAsync(t => t.Key == key, ct);
        return LoginThrottlePolicy.IsLocked(throttle, time.GetUtcNow()) ? throttle!.LockedUntil : null;
    }

    /// <summary>One wrong password or code; returns the end of the pause when this failure starts one.</summary>
    public async Task<DateTimeOffset?> FailAsync(string key, CancellationToken ct = default)
    {
        var throttle = await db.LoginThrottles.FirstOrDefaultAsync(t => t.Key == key, ct);
        if (throttle is null)
        {
            throttle = new LoginThrottle { Key = key };
            db.LoginThrottles.Add(throttle);
        }
        var lockedUntil = LoginThrottlePolicy.RegisterFailure(throttle, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return lockedUntil;
    }

    public Task ResetAsync(string key, CancellationToken ct = default) =>
        db.LoginThrottles.Where(t => t.Key == key).ExecuteDeleteAsync(ct);

    public static string? ClientIp(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip is null)
            return null;
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    public static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
