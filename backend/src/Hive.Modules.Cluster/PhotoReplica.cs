using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Cluster;

/// <summary>
/// Which photos go to the city first and how long a photo waits (spec 7.3): alarms first and for a week, other events for a day,
/// periodic snapshots and time-lapse frames for six hours. After a long blackout only the alarms are worth catching up on.
/// </summary>
public static class PhotoReplicaPolicy
{
    public const int Alarm = 1;
    public const int Event = 2;
    public const int Periodic = 3;

    public static int Priority(MediaKind kind, string? eventId, EventSeverity? severity) =>
        kind != MediaKind.Photo || eventId is null ? Periodic
        : severity >= EventSeverity.Alarm ? Alarm
        : Event;

    public static TimeSpan Ttl(int priority) => priority switch
    {
        Alarm => TimeSpan.FromDays(7),
        Event => TimeSpan.FromHours(24),
        _ => TimeSpan.FromHours(6),
    };

    /// <summary>A failed send is tried again after 1, 2, 4... minutes, at most an hour.</summary>
    public static TimeSpan Pause(int tries) => TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(tries - 1, 0, 6))));

    /// <summary>The header the city checks: made from the secret both nodes share, so nothing has to be handed over.</summary>
    public static string Key(string secret) => CityBrokerLogin.Password(secret, "replica-media");
}

/// <summary>What the Settings card shows of the photo copy to the city.</summary>
public sealed record PhotoReplicaStatusDto(bool Enabled, string? Url, int Waiting, int WaitingAlarms, DateTimeOffset? LastSentAt, string? LastError);

/// <summary>Remembers the last send and the last problem, for the Settings card.</summary>
public sealed class PhotoReplicaState
{
    public DateTimeOffset? LastSentAt { get; private set; }

    public string? LastError { get; private set; }

    public void Sent(DateTimeOffset at)
    {
        LastSentAt = at;
        LastError = null;
    }

    public void Failed(string error) => LastError = error;
}

/// <summary>
/// Home: sends the photos to the city (<c>PUT /api/ingest/replica/media/{id}</c>), alarms first. A photo leaves the queue once the city
/// has kept it; a failed one waits longer each time, and a city that cannot be reached at all stops the round.
/// </summary>
public sealed class PhotoReplicator(
    IOptions<ClusterOptions> options,
    IOptions<HiveOptions> hive,
    IOptions<CityBrokerOptions> secret,
    NodeState node,
    IServiceScopeFactory scopes,
    MediaStore store,
    PhotoReplicaState state,
    TimeProvider time,
    ILogger<PhotoReplicator> logger) : BackgroundService
{
    public const string UrlKey = "failover.city";
    private const int Batch = 10;

    private static readonly HttpClient Http = new(CityHttp.Handler(TimeSpan.FromSeconds(10))) { Timeout = TimeSpan.FromSeconds(60) };

    private bool Sending => options.Value.Enabled && hive.Value.Node == "home" && !string.IsNullOrEmpty(secret.Value.BridgePassword);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The build runs the app to write the OpenAPI document; it must not need a database.
        if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider" || !Sending)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.ReplicaEveryS)), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    state.Failed(ex.Message);
                    logger.LogWarning(ex, "Sending photos to the city failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The address of the city hive as home reaches it: the one set in Settings, else https:// and the city broker's address.</summary>
    public static string? CityUrl(string? settingJson)
    {
        if (settingJson is null)
            return null;
        using var doc = JsonDocument.Parse(settingJson);
        if (doc.RootElement.TryGetProperty("replicaUrl", out var url) && url.GetString() is { Length: > 0 } custom)
            return custom.TrimEnd('/');
        return doc.RootElement.TryGetProperty("host", out var host) && host.GetString() is { Length: > 0 } h ? $"https://{h}" : null;
    }

    internal async Task<int> TickAsync(CancellationToken ct)
    {
        if (!Sending || node.ReadOnly)
            return 0;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var url = CityUrl(await db.Modes.AsNoTracking().Where(m => m.Key == UrlKey).Select(m => m.Value).FirstOrDefaultAsync(ct));
        if (url is null)
            return 0;
        var now = time.GetUtcNow();
        var sent = 0;
        foreach (var priority in new[] { PhotoReplicaPolicy.Alarm, PhotoReplicaPolicy.Event, PhotoReplicaPolicy.Periodic })
        {
            var batch = await Waiting(db, priority, now).OrderBy(m => m.ReceivedAt).Take(Batch).ToListAsync(ct);
            foreach (var item in batch)
            {
                switch (await SendAsync(db, url, item, ct))
                {
                    case Outcome.Kept:
                        sent++;
                        break;
                    case Outcome.CityUnreachable:
                        return sent;   // no use trying the next photo: wait for the next round
                }
            }
            if (sent >= Batch)
                break;
        }
        return sent;
    }

    /// <summary>The photos of one priority that are not at the city yet, are not in a pause and are not past their time.</summary>
    internal static IQueryable<MediaItem> Waiting(HiveDbContext db, int priority, DateTimeOffset now)
    {
        var oldest = now - PhotoReplicaPolicy.Ttl(priority);
        var query =
            from m in db.Media
            where m.ReplicatedAt == null && m.ReceivedAt >= oldest && (m.ReplicaNextAt == null || m.ReplicaNextAt <= now)
            join e in db.Events on m.EventId equals e.Id into events
            from e in events.DefaultIfEmpty()
            select new { m, Severity = (EventSeverity?)e.Severity };
        // The severity is stored as text, so "at least an alarm" is spelled out.
        var alarms = new EventSeverity?[] { EventSeverity.Alarm, EventSeverity.Critical };
        return priority switch
        {
            PhotoReplicaPolicy.Alarm => query.Where(x => x.m.Kind == MediaKind.Photo && x.m.EventId != null && alarms.Contains(x.Severity)).Select(x => x.m),
            PhotoReplicaPolicy.Event => query.Where(x => x.m.Kind == MediaKind.Photo && x.m.EventId != null && !alarms.Contains(x.Severity)).Select(x => x.m),
            _ => query.Where(x => x.m.Kind != MediaKind.Photo || x.m.EventId == null).Select(x => x.m),
        };
    }

    private enum Outcome { Kept, Refused, CityUnreachable }

    private async Task<Outcome> SendAsync(HiveDbContext db, string url, MediaItem media, CancellationToken ct)
    {
        byte[] body;
        try
        {
            body = await File.ReadAllBytesAsync(store.FullPath(media.Path), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file is gone (cleanup): nothing to send, ever.
            media.ReplicaNextAt = time.GetUtcNow().AddYears(1);
            await db.SaveChangesAsync(ct);
            return Outcome.Refused;
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{url}/api/ingest/replica/media/{media.Id}") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        request.Headers.Add("X-Replica-Key", PhotoReplicaPolicy.Key(secret.Value.BridgePassword!));
        request.Headers.Add("X-Device-Id", media.DeviceId);
        request.Headers.Add("X-Kind", media.Kind.ToString());
        request.Headers.Add("X-Ts", media.Ts.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Sha256", media.Sha256);
        if (media.EventId is not null)
            request.Headers.Add("X-Event-Id", media.EventId);
        var eventType = media.EventId is null ? null : await db.Events.AsNoTracking().Where(e => e.Id == media.EventId).Select(e => e.Type).FirstOrDefaultAsync(ct);
        if (eventType is not null && Topics.IsValidSegment(eventType))
            request.Headers.Add("X-Event-Type", eventType);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            state.Failed($"the city does not answer: {ex.Message}");
            return Outcome.CityUnreachable;
        }
        using (response)
        {
            var now = time.GetUtcNow();
            if (response.IsSuccessStatusCode)
            {
                media.ReplicatedAt = now;
                await db.SaveChangesAsync(ct);
                state.Sent(now);
                return Outcome.Kept;
            }
            media.ReplicaTries++;
            media.ReplicaNextAt = now + PhotoReplicaPolicy.Pause(media.ReplicaTries);
            await db.SaveChangesAsync(ct);
            state.Failed($"the city refused {media.Id}: {(int)response.StatusCode}");
            logger.LogWarning("The city refused photo {Id}: {Status}", media.Id, (int)response.StatusCode);
            return Outcome.Refused;
        }
    }

    /// <summary>For the Settings card: what waits (and how many of it are alarms), when the last photo went and the last problem.</summary>
    public static async Task<PhotoReplicaStatusDto> StatusAsync(HiveDbContext db, PhotoReplicaState state, bool enabled, string? url, DateTimeOffset now, CancellationToken ct)
    {
        var alarms = await Waiting(db, PhotoReplicaPolicy.Alarm, now.AddYears(1)).CountAsync(ct);
        var events = await Waiting(db, PhotoReplicaPolicy.Event, now.AddYears(1)).CountAsync(ct);
        var periodic = await Waiting(db, PhotoReplicaPolicy.Periodic, now.AddYears(1)).CountAsync(ct);
        return new PhotoReplicaStatusDto(enabled, url, alarms + events + periodic, alarms, state.LastSentAt, state.LastError);
    }
}

/// <summary>City: keeps the photos home sends. Checks the key made from the shared secret, and that the bytes are the photo home named.</summary>
public sealed partial class PhotoReplicaReceiver(
    HiveDbContext db,
    MediaStore store,
    ThumbnailQueue thumbnails,
    IOptions<ClusterOptions> options,
    IOptions<HiveOptions> hive,
    IOptions<CityBrokerOptions> secret,
    IOptions<MediaOptions> mediaOptions,
    TimeProvider time)
{
    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{26}-[0-9]{1,2}$")]
    private static partial Regex PhotoId();

    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{26}$")]
    private static partial Regex Ulid();

    public async Task<IResult> ReceiveAsync(string id, HttpRequest request, CancellationToken ct)
    {
        if (!options.Value.Enabled || hive.Value.Node != "city" || string.IsNullOrEmpty(secret.Value.BridgePassword))
            return Results.NotFound();
        var key = request.Headers["X-Replica-Key"].ToString();
        if (key.Length == 0 || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(PhotoReplicaPolicy.Key(secret.Value.BridgePassword))))
            return Results.Unauthorized();

        var deviceId = request.Headers["X-Device-Id"].ToString();
        var eventId = request.Headers["X-Event-Id"].ToString();
        var eventType = request.Headers["X-Event-Type"].ToString();
        if (!PhotoId().IsMatch(id) || !Topics.IsValidDeviceId(deviceId) || (eventId.Length > 0 && !Ulid().IsMatch(eventId))
            || !Enum.TryParse<MediaKind>(request.Headers["X-Kind"].ToString(), out var kind) || !long.TryParse(request.Headers["X-Ts"].ToString(), out var ms))
            return Results.BadRequest("bad headers");

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();
        if (body.LongLength > mediaOptions.Value.MaxPhotoBytes * 4)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var sha = Convert.ToHexStringLower(SHA256.HashData(body));
        if (!PhotoIngestService.IsJpeg(body) || ImageTools.Identify(body) is not { } size || !string.Equals(sha, request.Headers["X-Sha256"].ToString(), StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("not the photo that was named");

        // Idempotent: the same photo twice is one photo.
        if (await db.Media.AnyAsync(m => m.Id == id, ct))
            return Results.Ok();

        var ts = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        var type = Topics.IsValidSegment(eventType) ? eventType : kind == MediaKind.Photo ? "event" : "snapshot";
        var media = new MediaItem
        {
            Id = id,
            EventId = eventId.Length > 0 ? eventId : null,
            DeviceId = deviceId,
            Kind = kind,
            Ts = ts,
            ReceivedAt = time.GetUtcNow(),
            Path = MediaStore.PhotoPath(deviceId, hive.Value.ToLocal(ts), type, id),
            Bytes = body.LongLength,
            Width = size.Width,
            Height = size.Height,
            Sha256 = sha,
            UploadedVia = "replica",
        };
        await store.WriteAtomicAsync(media.Path, body, ct);
        db.Media.Add(media);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Results.Ok();   // a concurrent send of the same photo won
        }
        thumbnails.Enqueue(media.Id);
        return Results.Created($"/api/media/{id}", null);
    }
}

public static class PhotoReplicaEndpoints
{
    public static IEndpointRouteBuilder MapPhotoReplicaEndpoints(this IEndpointRouteBuilder app)
    {
        // Home's node authenticates with the key made from the shared secret, not with a user session.
        app.MapPut("/api/ingest/replica/media/{id}", (string id, HttpRequest request, PhotoReplicaReceiver receiver, CancellationToken ct) => receiver.ReceiveAsync(id, request, ct))
            .AllowAnonymous()
            .NodeLocal()
            .DisableAntiforgery()
            .ExcludeFromDescription()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(12 * 1024 * 1024))
            .WithTags("replica");
        app.MapGet("/api/replica/media", async (HiveDbContext db, PhotoReplicaState state, IOptions<ClusterOptions> cluster, IOptions<HiveOptions> hive, IOptions<CityBrokerOptions> secret, TimeProvider time, CancellationToken ct) =>
            {
                var url = PhotoReplicator.CityUrl(await db.Modes.AsNoTracking().Where(m => m.Key == PhotoReplicator.UrlKey).Select(m => m.Value).FirstOrDefaultAsync(ct));
                var enabled = cluster.Value.Enabled && hive.Value.Node == "home" && !string.IsNullOrEmpty(secret.Value.BridgePassword) && url is not null;
                return TypedResults.Ok(await PhotoReplicator.StatusAsync(db, state, enabled, url, time.GetUtcNow(), ct));
            })
            .RequireAuthorization(HiveRoles.AdminOnly)
            .WithTags("replica");
        return app;
    }
}
