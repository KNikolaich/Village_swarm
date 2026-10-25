using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Hive.Contracts.Messages;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Modules.Devices;

/// <summary>Whether a monitor may read its screen from outside, and its last read.</summary>
public sealed record ScreenAccessDto(bool Enabled, DateTimeOffset? IssuedAt, DateTimeOffset? LastReadAt, string? LastReadFrom, bool LastReadExternal, string Path);

/// <summary>A new screen token: shown once, the hive keeps only its hash.</summary>
public sealed record ScreenTokenDto(string Token, ScreenAccessDto Access);

public enum ScreenAccessStatus
{
    Ok,
    NotFound,
    NotMonitor,
}

/// <summary>
/// Read-only access of a monitor to its own screen from anywhere (contracts/monitor-http.md): a token per monitor, issued and
/// revoked on its page. The token opens only the texts the hive would publish to vs/v1/hive/display/{id}.
/// </summary>
public sealed class ScreenAccessService(HiveDbContext db, DisplayFeedService feed, NodeState node, TimeProvider time)
{
    public const string MonitorType = "monitor";
    public const string FramePath = "/api/monitor/v1/frame";

    /// <summary>A read is noted at most this often (an e-ink panel may ask every minute; the RPi's card need not hear about each).</summary>
    private static readonly TimeSpan NoteEvery = TimeSpan.FromMinutes(5);

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static ScreenAccessDto Dto(Device d) =>
        new(d.ScreenTokenHash is not null, d.ScreenTokenIssuedAt, d.ScreenReadAt, d.ScreenReadFrom, d.ScreenReadExternal, FramePath);

    private Task<Device?> FindAsync(string deviceId, CancellationToken ct) =>
        db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId && (d.Driver == Drivers.Hornet || d.Driver == Drivers.Http) && d.Status != DeviceStatus.Disabled, ct);

    public async Task<ScreenAccessDto?> GetAsync(string deviceId, CancellationToken ct) =>
        await FindAsync(deviceId, ct) is { } d ? Dto(d) : null;

    /// <summary>A new token for a monitor; the old one stops working at once.</summary>
    public async Task<(ScreenAccessStatus Status, ScreenTokenDto? Token)> IssueAsync(string deviceId, string by, CancellationToken ct)
    {
        var device = await FindAsync(deviceId, ct);
        if (device is null)
            return (ScreenAccessStatus.NotFound, null);
        if (device.TypeCode != MonitorType)
            return (ScreenAccessStatus.NotMonitor, null);
        // 32 random bytes, URL-safe: easy to paste into a panel's settings, impossible to guess.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        device.ScreenTokenHash = Hash(token);
        device.ScreenTokenIssuedAt = time.GetUtcNow();
        device.ScreenReadAt = null;
        device.ScreenReadFrom = null;
        device.ScreenReadExternal = false;
        Audit.Record(db, time.GetUtcNow(), by, "screen_token_issued", deviceId, null);
        await db.SaveChangesAsync(ct);
        return (ScreenAccessStatus.Ok, new ScreenTokenDto(token, Dto(device)));
    }

    public async Task<bool> RevokeAsync(string deviceId, string by, CancellationToken ct)
    {
        var device = await FindAsync(deviceId, ct);
        if (device is null)
            return false;
        if (device.ScreenTokenHash is not null)
        {
            device.ScreenTokenHash = null;
            device.ScreenTokenIssuedAt = null;
            Audit.Record(db, time.GetUtcNow(), by, "screen_token_revoked", deviceId, null);
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    /// <summary>
    /// The frame for a bearer token, or null for a token that opens nothing. JSON: the display message; bitmap: the picture
    /// the hive draws, one bit a pixel (only for a screen the hive draws). The ETag follows the items only, not the time.
    /// The read is noted on the device; a monitor far away is seen by its reads.
    /// </summary>
    public async Task<ScreenFrame?> FrameAsync(string? bearer, bool bitmap, string? from, bool external, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(bearer))
            return null;
        var hash = Hash(bearer);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.ScreenTokenHash == hash && (d.Driver == Drivers.Hornet || d.Driver == Drivers.Http)
            && d.Status != DeviceStatus.Disabled && d.TypeCode == MonitorType, ct);
        if (device is null)
            return null;
        var screen = DisplayScreens.For(device.Hw);
        if (bitmap && screen is not { Rendered: true })
            return new ScreenFrame([], "", "", NoPicture: true);
        var message = await feed.BuildAsync(device, ct);
        var items = JsonSerializer.Serialize(message.Items, ContractJson.Options);
        var etag = "\"" + (bitmap ? "b" : "j") + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(items)))[..16] + "\"";

        // The city only watches (its copy of the database is not written): it answers but does not note the read.
        var now = time.GetUtcNow();
        if (!node.ReadOnly && (device.ScreenReadAt is not { } last || now - last >= NoteEvery || device.ScreenReadFrom != from || device.ScreenReadExternal != external
            || (device.Driver == Drivers.Http && device.Status != DeviceStatus.Online)))
        {
            device.ScreenReadAt = now;
            device.ScreenReadFrom = from is { Length: > 64 } f ? f[..64] : from;
            device.ScreenReadExternal = external;
            if (device.Driver == Drivers.Http)
            {
                device.LastSeenAt = now;
                device.Status = DeviceStatus.Online;
            }
            await db.SaveChangesAsync(ct);
        }
        if (!bitmap)
            return new ScreenFrame(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, ContractJson.Options)), "application/json", etag);
        using var picture = DisplayRender.Render(message, screen!);
        return new ScreenFrame(DisplayRender.Pack(picture), "application/octet-stream", etag, screen!.Width, screen.Height);
    }

    private static readonly System.Text.RegularExpressions.Regex DeviceIdPattern = new("^[a-z][a-z0-9]*(-[a-z0-9]+)*$");

    /// <summary>
    /// A monitor far away that speaks only HTTP (an e-ink panel at another place): added by hand, it gets its screen token on
    /// its page and is seen when it reads. Its board says the screen the hive draws for it.
    /// </summary>
    public async Task<(Device? Device, string? Error)> AddRemoteAsync(string deviceId, string name, string board, string by, CancellationToken ct)
    {
        deviceId = deviceId.Trim();
        if (deviceId.Length is 0 or > 32 || !DeviceIdPattern.IsMatch(deviceId))
            return (null, "id: латиница в нижнем регистре, цифры и дефис, до 32 знаков, например monitor-city1");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
            return (null, "имя: от 1 до 128 знаков");
        if (!DisplayScreens.RenderedBoards.Contains(board))
            return (null, "плата: " + string.Join(", ", DisplayScreens.RenderedBoards));
        if (await db.Devices.AnyAsync(d => d.DeviceId == deviceId, ct))
            return (null, "устройство с таким id уже есть");
        var now = time.GetUtcNow();
        var device = new Device
        {
            DeviceId = deviceId, TypeCode = MonitorType, Name = name.Trim(), Hw = board, Driver = Drivers.Http,
            Status = DeviceStatus.Pending, CreatedAt = now,
        };
        db.Devices.Add(device);
        Audit.Record(db, now, by, "remote_monitor_added", deviceId, new { board });
        await db.SaveChangesAsync(ct);
        return (device, null);
    }
}

/// <summary>A frame for a monitor: the body, its type and ETag; the size for a bitmap. NoPicture: the hive does not draw this screen.</summary>
public sealed record ScreenFrame(byte[] Body, string ContentType, string ETag, int Width = 0, int Height = 0, bool NoPicture = false);

public sealed record AddRemoteMonitorRequest(string DeviceId, string Name, string Board);

public static class ScreenAccessEndpoints
{
    public const string RateLimit = "screen";

    /// <summary>Set only by the router's site in Caddy (:8088): the request came from the internet (AuthService.ExternalHeader).</summary>
    private const string ExternalHeader = "X-Hive-External";

    /// <summary>Reads a minute per address: a panel needs one a minute at most, a guesser gets nowhere.</summary>
    public const int ReadsPerMinute = 12;

    public static IServiceCollection AddScreenAccess(this IServiceCollection services)
    {
        services.AddScoped<ScreenAccessService>();
        services.AddRateLimiter(o => o.AddPolicy(RateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = ReadsPerMinute, Window = TimeSpan.FromMinutes(1) })));
        return services;
    }

    public static IEndpointRouteBuilder MapScreenAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var devices = app.MapGroup("/api/devices/{deviceId}/screen-access").WithTags("devices").RequireAuthorization(HiveRoles.AdminOnly);
        devices.MapGet("", async Task<Results<Ok<ScreenAccessDto>, NotFound>> (string deviceId, ScreenAccessService access, CancellationToken ct) =>
            await access.GetAsync(deviceId, ct) is { } dto ? TypedResults.Ok(dto) : TypedResults.NotFound());
        devices.MapPost("", async Task<Results<Ok<ScreenTokenDto>, NotFound, ValidationProblem>> (
                string deviceId, ClaimsPrincipal user, ScreenAccessService access, CancellationToken ct) =>
            {
                var (status, token) = await access.IssueAsync(deviceId, $"user:{user.Identity?.Name}", ct);
                return status switch
                {
                    ScreenAccessStatus.Ok => TypedResults.Ok(token!),
                    ScreenAccessStatus.NotFound => TypedResults.NotFound(),
                    _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["Доступ снаружи бывает только у мониторов"] }),
                };
            });
        devices.MapDelete("", async Task<Results<NoContent, NotFound>> (string deviceId, ClaimsPrincipal user, ScreenAccessService access, CancellationToken ct) =>
            await access.RevokeAsync(deviceId, $"user:{user.Identity?.Name}", ct) ? TypedResults.NoContent() : TypedResults.NotFound());

        app.MapPost("/api/devices/remote-monitors", async Task<Results<Created<DeviceDto>, ValidationProblem>> (
                AddRemoteMonitorRequest request, ClaimsPrincipal user, ScreenAccessService access, DeviceQueries q, CancellationToken ct) =>
            {
                var (device, error) = await access.AddRemoteAsync(request.DeviceId, request.Name, request.Board, $"user:{user.Identity?.Name}", ct);
                return device is null
                    ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["deviceId"] = [error!] })
                    : TypedResults.Created($"/api/devices/{device.DeviceId}", (await q.GetAsync(device.DeviceId, ct))!);
            })
            .WithTags("devices")
            .RequireAuthorization(HiveRoles.AdminOnly);

        // The monitor itself, from anywhere: its bearer token, no session (contracts/monitor-http.md). frame: the texts as
        // JSON; frame.bin: the picture the hive draws, for e-ink panels.
        app.MapGet(ScreenAccessService.FramePath, (HttpContext http, ScreenAccessService access, CancellationToken ct) => Frame(http, access, false, ct))
            .AllowAnonymous()
            .RequireRateLimiting(RateLimit)
            .WithTags("monitor")
            .ExcludeFromDescription();
        app.MapGet(ScreenAccessService.FramePath + ".bin", (HttpContext http, ScreenAccessService access, CancellationToken ct) => Frame(http, access, true, ct))
            .AllowAnonymous()
            .RequireRateLimiting(RateLimit)
            .WithTags("monitor")
            .ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> Frame(HttpContext http, ScreenAccessService access, bool bitmap, CancellationToken ct)
    {
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        var frame = await access.FrameAsync(token, bitmap, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.ContainsKey(ExternalHeader), ct);
        http.Response.Headers.CacheControl = "no-store";
        if (frame is null)
            return Results.Unauthorized();
        if (frame.NoPicture)
            return Results.NotFound();
        http.Response.Headers.ETag = frame.ETag;
        if (bitmap)
        {
            http.Response.Headers["X-Frame-Width"] = frame.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
            http.Response.Headers["X-Frame-Height"] = frame.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (http.Request.Headers.IfNoneMatch.ToString() == frame.ETag)
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(frame.Body, frame.ContentType);
    }
}
