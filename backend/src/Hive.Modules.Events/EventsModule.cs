using System.Security.Claims;
using System.Text.Json;
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

namespace Hive.Modules.Events;

/// <summary>Ts is when the frame was taken; a camera that could not take a fresh one sends an older frame with its own time.</summary>
public sealed record EventPhotoDto(string Id, string Url, string ThumbUrl, DateTimeOffset Ts);

public sealed record EventDto(
    string Id, string DeviceId, string? DeviceName, string? Zone, string Type, string Severity,
    DateTimeOffset Ts, DateTimeOffset ReceivedAt, bool? Armed,
    string? AcknowledgedBy, DateTimeOffset? AcknowledgedAt, JsonElement Payload, IReadOnlyList<EventPhotoDto> Photos,
    string? PhotoProblem = null);

public sealed record EventFilter(
    string? Type, string? Device, int? Zone, string? Severity, DateTimeOffset? From, DateTimeOffset? To, string? Cursor, int? Limit);

/// <summary>Event feed and acknowledgement (spec 6.5, 6.7).</summary>
public sealed record AckAllRequest(string? Device = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

public sealed record AckAllResult(int Acknowledged);

public sealed class EventQueries(HiveDbContext db, TimeProvider time)
{
    public async Task<Page<EventDto>> ListAsync(EventFilter f, CancellationToken ct)
    {
        var take = TsCursor.ClampLimit(f.Limit);
        var query = db.Events.AsNoTracking();
        if (!string.IsNullOrEmpty(f.Type))
            query = query.Where(e => e.Type == f.Type);
        else // timelapse frames go to the photos, not to the feed (a camera every 5 minutes would bury everything else)
            query = query.Where(e => !db.Media.Any(m => m.EventId == e.Id && m.Kind == MediaKind.Timelapse));
        if (!string.IsNullOrEmpty(f.Device))
            query = query.Where(e => e.DeviceId == f.Device);
        if (f.Zone is { } zone)
            query = query.Where(e => db.Devices.Any(d => d.DeviceId == e.DeviceId && d.ZoneId == zone));
        if (Enum.TryParse<EventSeverity>(f.Severity, ignoreCase: true, out var minSeverity))
            query = query.Where(e => e.Severity >= minSeverity);
        if (f.From?.ToUniversalTime() is { } from)
            query = query.Where(e => e.Ts >= from);
        if (f.To?.ToUniversalTime() is { } to)
            query = query.Where(e => e.Ts < to);
        if (TsCursor.Decode(f.Cursor) is { } c)
            query = query.Where(e => e.Ts < c.Ts || (e.Ts == c.Ts && string.Compare(e.Id, c.Id) < 0));

        var events = await query.OrderByDescending(e => e.Ts).ThenByDescending(e => e.Id).Take(take + 1).ToListAsync(ct);
        var next = events.Count > take ? new TsCursor(events[take - 1].Ts, events[take - 1].Id).Encode() : null;
        events = events.Take(take).ToList();
        return new Page<EventDto>(await ToDtosAsync(events, ct), next);
    }

    public async Task<EventDto?> GetAsync(string id, CancellationToken ct)
    {
        var e = await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? null : (await ToDtosAsync([e], ct))[0];
    }

    /// <summary>POST /api/events/{id}/ack. Idempotent: the first acknowledgement wins.</summary>
    public async Task<EventDto?> AcknowledgeAsync(string id, string by, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await db.Events.Where(e => e.Id == id && e.AcknowledgedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.AcknowledgedAt, now).SetProperty(e => e.AcknowledgedBy, by), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Marks every unseen event in the filter as viewed; returns how many changed. Same meaning on the web and in Telegram.</summary>
    public async Task<int> AcknowledgeManyAsync(AckAllRequest r, string by, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var query = db.Events.Where(e => e.AcknowledgedAt == null);
        if (!string.IsNullOrEmpty(r.Device))
            query = query.Where(e => e.DeviceId == r.Device);
        if (r.From is { } from)
            query = query.Where(e => e.Ts >= from);
        if (r.To is { } to)
            query = query.Where(e => e.Ts < to);
        return await query.ExecuteUpdateAsync(s => s.SetProperty(e => e.AcknowledgedAt, now).SetProperty(e => e.AcknowledgedBy, by), ct);
    }

    private async Task<List<EventDto>> ToDtosAsync(List<DeviceEvent> events, CancellationToken ct)
    {
        var ids = events.Select(e => e.Id).ToList();
        var deviceIds = events.Select(e => e.DeviceId).Distinct().ToList();
        var photos = (await db.Media.AsNoTracking()
                .Where(m => m.EventId != null && ids.Contains(m.EventId))
                .Select(m => new { m.EventId, m.Id, m.Ts })
                .ToListAsync(ct))
            .GroupBy(m => m.EventId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Id, StringComparer.Ordinal)
                .Select(m => new EventPhotoDto(m.Id, $"/api/media/{m.Id}", $"/api/media/{m.Id}/thumb", m.Ts)).ToList());
        var devices = await db.Devices.AsNoTracking()
            .Where(d => deviceIds.Contains(d.DeviceId))
            .Select(d => new { d.DeviceId, d.Name, Zone = d.Zone == null ? null : d.Zone.Name })
            .ToDictionaryAsync(d => d.DeviceId, ct);

        var noFrame = await NoFrameAsync(events.Where(e => !photos.ContainsKey(e.Id)).ToList(), ct);

        return events.Select(e =>
        {
            devices.TryGetValue(e.DeviceId, out var d);
            return new EventDto(e.Id, e.DeviceId, d?.Name, d?.Zone, e.Type, e.Severity.ToString().ToLowerInvariant(),
                e.Ts, e.ReceivedAt, e.Armed, e.AcknowledgedBy, e.AcknowledgedAt, e.Payload.RootElement.Clone(),
                photos.GetValueOrDefault(e.Id) ?? [], noFrame.Contains(e.Id) ? NoFramePhotoProblem : null);
        }).ToList();
    }

    /// <summary>PhotoProblem of an event whose camera reported it could take no frame: a power problem far more often than Wi-Fi.</summary>
    public const string NoFramePhotoProblem = "no_frame";

    /// <summary>Message a guard camera logs when the sensor gives no frame (firmware guard_cam.cpp).</summary>
    public const string CameraFrameFailedLog = "camera frame failed";

    /// <summary>
    /// Events without photos whose camera, around the event, logged that it got no frame or came back from a brownout
    /// reset: the photo was never taken, so the card should not blame the upload.
    /// </summary>
    private async Task<HashSet<string>> NoFrameAsync(List<DeviceEvent> events, CancellationToken ct)
    {
        var result = new HashSet<string>();
        if (events.Count == 0)
            return result;
        var before = TimeSpan.FromSeconds(10);
        var after = TimeSpan.FromMinutes(3);
        var deviceIds = events.Select(e => e.DeviceId).Distinct().ToList();
        var from = events.Min(e => e.Ts) - before;
        var to = events.Max(e => e.Ts) + after;
        var marks = await db.DeviceLogs.AsNoTracking()
            .Where(l => deviceIds.Contains(l.DeviceId) && l.Ts >= from && l.Ts <= to && l.Msg == CameraFrameFailedLog)
            .Select(l => new { l.DeviceId, l.Ts })
            .ToListAsync(ct);
        // Health repeats the reset reason of its boot for the whole uptime: the reboot itself is what counts.
        marks.AddRange((await db.DeviceHealth.AsNoTracking()
                .Where(h => deviceIds.Contains(h.DeviceId) && h.Ts >= from && h.ResetReason == "brownout")
                .Select(h => new { h.DeviceId, h.Ts, h.UptimeS })
                .ToListAsync(ct))
            .Select(h => new { h.DeviceId, Ts = h.Ts - TimeSpan.FromSeconds(h.UptimeS) })
            .Where(h => h.Ts >= from && h.Ts <= to));
        foreach (var e in events)
            if (marks.Any(m => m.DeviceId == e.DeviceId && m.Ts >= e.Ts - before && m.Ts <= e.Ts + after))
                result.Add(e.Id);
        return result;
    }
}

public static class EventsModule
{
    public const string Name = "events";

    public static IServiceCollection AddEventsModule(this IServiceCollection services) =>
        services.AddScoped<EventQueries>();

    public static IEndpointRouteBuilder MapEventsEndpoints(this IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/api/events").WithTags("events");
        events.MapGet("", (string? type, string? device, int? zone, string? severity, DateTimeOffset? from, DateTimeOffset? to,
                string? cursor, int? limit, EventQueries q, CancellationToken ct) =>
            q.ListAsync(new EventFilter(type, device, zone, severity, from, to, cursor, limit), ct));
        events.MapGet("/{id}", async Task<Results<Ok<EventDto>, NotFound>> (string id, EventQueries q, CancellationToken ct) =>
            await q.GetAsync(id, ct) is { } e ? TypedResults.Ok(e) : TypedResults.NotFound());
        events.MapPost("/{id}/ack", async Task<Results<Ok<EventDto>, NotFound>> (string id, ClaimsPrincipal user, EventQueries q, CancellationToken ct) =>
                await q.AcknowledgeAsync(id, $"user:{user.Identity?.Name}", ct) is { } e ? TypedResults.Ok(e) : TypedResults.NotFound())
            .RequireAuthorization(HiveRoles.CanControl);
        // "Отметить все просмотренными": the filter of the feed (device, from, to). Body may be empty.
        events.MapPost("/ack", async Task<Ok<AckAllResult>> (AckAllRequest? request, ClaimsPrincipal user, EventQueries q, CancellationToken ct) =>
                TypedResults.Ok(new AckAllResult(await q.AcknowledgeManyAsync(request ?? new AckAllRequest(), $"user:{user.Identity?.Name}", ct))))
            .RequireAuthorization(HiveRoles.CanControl);
        return app;
    }
}
