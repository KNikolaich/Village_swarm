using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Hive.Contracts;
using Hive.Contracts.Messages;
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
public sealed class ScreenAccessService(HiveDbContext db, DisplayFeedService feed, NodeState node, IngestQueue ingest, TimeProvider time)
{
    public const string MonitorType = "monitor";
    public const string FramePath = "/api/monitor/v1/frame";

    /// <summary>A read is noted at most this often (an e-ink panel may ask every minute; the RPi's card need not hear about each).</summary>
    private static readonly TimeSpan NoteEvery = TimeSpan.FromMinutes(5);

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(token))));

    // Letters and digits that cannot be mistaken for each other (no 0/o, 1/l/i): a token read off a screen or typed on a phone.
    private const string TokenAlphabet = "23456789abcdefghjkmnpqrstuvwxyz";
    private const int TokenLength = 20;

    /// <summary>
    /// A token as people write it ("abcd-efgh-...", any case, spaces) is the same token. Only the short kind is touched:
    /// the long URL-safe tokens issued before are case-sensitive and kept as they are.
    /// </summary>
    public static string Normalize(string token)
    {
        var t = token.Trim();
        return t.Length <= 29 ? t.Replace("-", "").Replace(" ", "").ToLowerInvariant() : t;
    }

    /// <summary>20 random characters (about 99 bits), shown in groups of four: short enough to type, far beyond guessing at 12 reads a minute.</summary>
    public static string NewToken()
    {
        var raw = string.Create(TokenLength, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = TokenAlphabet[RandomNumberGenerator.GetInt32(TokenAlphabet.Length)];
        });
        return string.Join('-', Enumerable.Range(0, TokenLength / 4).Select(g => raw.Substring(g * 4, 4)));
    }

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
        var token = NewToken();
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
    /// the hive draws, one bit a pixel (only for a screen the hive draws). The ETag follows the items, background, turn and fonts, not the time.
    /// The read is noted on the device; a monitor far away is seen by its reads.
    /// </summary>
    public async Task<ScreenFrame?> FrameAsync(string? bearer, bool bitmap, string? from, bool external, CancellationToken ct) =>
        await FrameAsync(bearer, bitmap, from, external, null, null, ct);

    /// <summary>The panel's battery, as it says it (X-Battery-mV): what a lithium cell can show, else nothing.</summary>
    public const int MinBatteryMv = 2500, MaxBatteryMv = 4500;

    /// <summary>
    /// As above, for one <paramref name="tab"/> of a panel with tabs (0 when absent; beyond its tabs: no picture), noting the
    /// battery the panel told (millivolts) as its battery_voltage reading.
    /// </summary>
    public async Task<ScreenFrame?> FrameAsync(string? bearer, bool bitmap, string? from, bool external, int? tab, int? batteryMv, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(bearer))
            return null;
        var hash = Hash(bearer);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.ScreenTokenHash == hash && (d.Driver == Drivers.Hornet || d.Driver == Drivers.Http)
            && d.Status != DeviceStatus.Disabled && d.TypeCode == MonitorType, ct);
        if (device is null)
            return null;
        var screen = DisplayScreens.For(device.Hw);  // the panel's own size: the frame is turned to it
        if (bitmap && screen is not { Rendered: true })
            return new ScreenFrame([], "", "", NoPicture: true);
        var message = await feed.BuildAsync(device, ct);
        var now = time.GetUtcNow();
        if (batteryMv is >= MinBatteryMv and <= MaxBatteryMv && !node.ReadOnly)
            ingest.Enqueue(new TelemetryReceived(device.DeviceId, Ulid.New(now), now, now, [("battery_voltage", batteryMv.Value / 1000.0)]));
        var tabs = screen?.Tabs ?? 0;
        var shown = tab ?? 0;
        if (bitmap && tabs > 0 && (shown < 0 || shown >= tabs))
            return new ScreenFrame([], "", "", NoPicture: true);
        var items = JsonSerializer.Serialize(message.Items, ContractJson.Options);
        // the picture is also what is behind and around the items: the background, the turn, the fonts
        var basis = bitmap ? items + "|" + await feed.PictureTagAsync(device, ct) : items;
        var etag = "\"" + (bitmap ? "b" : "j") + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(basis)))[..16] + "\"";

        // The city only watches (its copy of the database is not written): it answers but does not note the read.
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
        // monitoring: the panel reads this often until it is over, then at its own interval again
        var refresh = DisplayFeedService.MonitorOf(device, now) is { } monitor ? monitor.EveryMin * 60 : 0;
        if (!bitmap)
            return new ScreenFrame(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, ContractJson.Options)), "application/json", etag, RefreshS: refresh);
        var rotation = DisplayFeedService.RotationOf(device);
        using var drawn = tabs > 0 ? await feed.DrawTabAsync(device, message, shown, ct) : await feed.DrawAsync(device, message, ct);
        if (drawn is null)
            return new ScreenFrame([], "", "", NoPicture: true);
        using var turned = DisplayRender.Rotate(drawn, rotation);
        var bits = DisplayRender.Pack(turned);
        // A tab holds the clock and the weather besides the items: its ETag is the picture itself.
        if (tabs > 0)
            etag = $"\"t{shown}" + Convert.ToHexStringLower(SHA256.HashData(bits))[..16] + "\"";
        return new ScreenFrame(bits, "application/octet-stream", etag, screen!.Width, screen.Height, Tabs: tabs, Rotation: rotation, RefreshS: refresh);
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
/// Tabs and Rotation: how many tabs the board has and how the picture was turned (X-Frame-Tabs, X-Frame-Rotation).
/// RefreshS: seconds to the next read while the panel is monitored (X-Frame-Refresh); 0 otherwise.
public sealed record ScreenFrame(byte[] Body, string ContentType, string ETag, int Width = 0, int Height = 0, bool NoPicture = false, int Tabs = 0, int Rotation = 0,
    int RefreshS = 0);

public sealed record AddRemoteMonitorRequest(string DeviceId, string Name, string Board);

public static class ScreenAccessEndpoints
{
    public const string RateLimit = "screen";

    /// <summary>Set only by the router's site in Caddy (:8088): the request came from the internet (AuthService.ExternalHeader).</summary>
    private const string ExternalHeader = "X-Hive-External";

    /// <summary>The panel's battery in millivolts, with every read (contracts/monitor-http.md).</summary>
    private const string BatteryHeader = "X-Battery-mV";

    /// <summary>Reads a minute per address: a panel needs one a minute at most, a guesser gets nowhere.</summary>
    public const int ReadsPerMinute = 12;

    /// <summary>Screen:ReadsPerMinute overrides <see cref="ReadsPerMinute"/> (tests read many frames from one address).</summary>
    public static IServiceCollection AddScreenAccess(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var readsPerMinute = int.TryParse(configuration["Screen:ReadsPerMinute"], out var n) && n > 0 ? n : ReadsPerMinute;
        services.AddScoped<ScreenAccessService>();
        services.AddRateLimiter(o => o.AddPolicy(RateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = readsPerMinute, Window = TimeSpan.FromMinutes(1) })));
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
        int? tab = int.TryParse(http.Request.Query["tab"], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null;
        int? battery = int.TryParse(http.Request.Headers[BatteryHeader], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var mv) ? mv : null;
        var frame = await access.FrameAsync(token, bitmap, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.ContainsKey(ExternalHeader), tab, battery, ct);
        http.Response.Headers.CacheControl = "no-store";
        if (frame is null)
            return Results.Unauthorized();
        if (frame.NoPicture)
            return Results.NotFound();
        http.Response.Headers.ETag = frame.ETag;
        if (frame.RefreshS > 0)
            http.Response.Headers["X-Frame-Refresh"] = frame.RefreshS.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (bitmap)
        {
            http.Response.Headers["X-Frame-Width"] = frame.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
            http.Response.Headers["X-Frame-Height"] = frame.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (frame.Tabs > 0)
            {
                http.Response.Headers["X-Frame-Tabs"] = frame.Tabs.ToString(System.Globalization.CultureInfo.InvariantCulture);
                http.Response.Headers["X-Frame-Rotation"] = frame.Rotation.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (frame.Tabs == ScreenTabs.TabWeights.Length)
                    http.Response.Headers["X-Frame-Tab-Weights"] = ScreenTabs.TabWeightsHeader;
            }
        }
        if (http.Request.Headers.IfNoneMatch.ToString() == frame.ETag)
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(frame.Body, frame.ContentType);
    }
}
