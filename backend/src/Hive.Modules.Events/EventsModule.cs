using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Modules.Events;

public sealed record EventPhotoDto(string Id, string Url, string ThumbUrl);

public sealed record EventDto(
    string Id, string DeviceId, string? DeviceName, string? Zone, string Type, string Severity,
    DateTimeOffset Ts, DateTimeOffset ReceivedAt, bool? Armed,
    string? AcknowledgedBy, DateTimeOffset? AcknowledgedAt, JsonElement Payload, IReadOnlyList<EventPhotoDto> Photos);

public sealed record EventFilter(
    string? Type, string? Device, int? Zone, string? Severity, DateTimeOffset? From, DateTimeOffset? To, string? Cursor, int? Limit);

/// <summary>Event feed and acknowledgement (spec 6.5, 6.7).</summary>
public sealed class EventQueries(HiveDbContext db, TimeProvider time)
{
    public async Task<Page<EventDto>> ListAsync(EventFilter f, CancellationToken ct)
    {
        var take = TsCursor.ClampLimit(f.Limit);
        var query = db.Events.AsNoTracking();
        if (!string.IsNullOrEmpty(f.Type))
            query = query.Where(e => e.Type == f.Type);
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

    private async Task<List<EventDto>> ToDtosAsync(List<DeviceEvent> events, CancellationToken ct)
    {
        var ids = events.Select(e => e.Id).ToList();
        var deviceIds = events.Select(e => e.DeviceId).Distinct().ToList();
        var photos = (await db.Media.AsNoTracking()
                .Where(m => m.EventId != null && ids.Contains(m.EventId))
                .Select(m => new { m.EventId, m.Id })
                .ToListAsync(ct))
            .GroupBy(m => m.EventId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Id, StringComparer.Ordinal)
                .Select(m => new EventPhotoDto(m.Id, $"/api/media/{m.Id}", $"/api/media/{m.Id}/thumb")).ToList());
        var devices = await db.Devices.AsNoTracking()
            .Where(d => deviceIds.Contains(d.DeviceId))
            .Select(d => new { d.DeviceId, d.Name, Zone = d.Zone == null ? null : d.Zone.Name })
            .ToDictionaryAsync(d => d.DeviceId, ct);

        return events.Select(e =>
        {
            devices.TryGetValue(e.DeviceId, out var d);
            return new EventDto(e.Id, e.DeviceId, d?.Name, d?.Zone, e.Type, e.Severity.ToString().ToLowerInvariant(),
                e.Ts, e.ReceivedAt, e.Armed, e.AcknowledgedBy, e.AcknowledgedAt, e.Payload.RootElement.Clone(),
                photos.GetValueOrDefault(e.Id) ?? []);
        }).ToList();
    }
}

public static class EventsModule
{
    public const string Name = "events";

    public static IServiceCollection AddEventsModule(this IServiceCollection services) =>
        services.AddScoped<EventQueries>();

    public static IEndpointRouteBuilder MapEventsEndpoints(this IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/api/events");
        events.MapGet("", (string? type, string? device, int? zone, string? severity, DateTimeOffset? from, DateTimeOffset? to,
                string? cursor, int? limit, EventQueries q, CancellationToken ct) =>
            q.ListAsync(new EventFilter(type, device, zone, severity, from, to, cursor, limit), ct));
        events.MapGet("/{id}", async (string id, EventQueries q, CancellationToken ct) =>
            await q.GetAsync(id, ct) is { } e ? Results.Ok(e) : Results.NotFound());
        // Until authentication (build step 6) the acknowledging user is "admin".
        events.MapPost("/{id}/ack", async (string id, EventQueries q, CancellationToken ct) =>
            await q.AcknowledgeAsync(id, "user:admin", ct) is { } e ? Results.Ok(e) : Results.NotFound());
        return app;
    }
}
