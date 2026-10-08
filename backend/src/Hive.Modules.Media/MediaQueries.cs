using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Media;

public sealed record MediaDto(
    string Id, string DeviceId, string? EventId, string Kind, DateTimeOffset Ts,
    int Width, int Height, long Bytes, bool Pinned, string Url, string ThumbUrl)
{
    public static MediaDto From(MediaItem m) => new(
        m.Id, m.DeviceId, m.EventId, m.Kind.ToString().ToLowerInvariant(), m.Ts, m.Width, m.Height, m.Bytes, m.Pinned,
        $"/api/media/{m.Id}", $"/api/media/{m.Id}/thumb");
}

/// <summary>Photos per calendar day in the house time zone, for the archive calendar.</summary>
public sealed record MediaDayDto(DateOnly Date, int Count);

public sealed class MediaQueries(HiveDbContext db, IOptions<HiveOptions> hive)
{
    /// <summary>GET /api/media?device=&amp;date=&amp;cursor= (spec 6.5), newest first.</summary>
    public async Task<Page<MediaDto>> ListAsync(string? device, DateOnly? date, string? eventId, string? cursor, int? limit, CancellationToken ct)
    {
        var take = TsCursor.ClampLimit(limit);
        var query = db.Media.AsNoTracking();
        if (!string.IsNullOrEmpty(device))
            query = query.Where(m => m.DeviceId == device);
        if (!string.IsNullOrEmpty(eventId))
            query = query.Where(m => m.EventId == eventId);
        if (date is { } day)
        {
            var (from, to) = hive.Value.DayRange(day);
            query = query.Where(m => m.Ts >= from && m.Ts < to);
        }
        if (TsCursor.Decode(cursor) is { } c)
            query = query.Where(m => m.Ts < c.Ts || (m.Ts == c.Ts && string.Compare(m.Id, c.Id) < 0));

        var items = await query.OrderByDescending(m => m.Ts).ThenByDescending(m => m.Id).Take(take + 1).ToListAsync(ct);
        var next = items.Count > take ? new TsCursor(items[take - 1].Ts, items[take - 1].Id).Encode() : null;
        return new Page<MediaDto>(items.Take(take).Select(MediaDto.From).ToList(), next);
    }

    /// <summary>GET /api/media/days?from=&amp;to=: days that have photos, with counts.</summary>
    public async Task<IReadOnlyList<MediaDayDto>> DaysAsync(DateOnly from, DateOnly to, string? device, CancellationToken ct)
    {
        var (start, _) = hive.Value.DayRange(from);
        var (_, end) = hive.Value.DayRange(to);
        var query = db.Media.AsNoTracking().Where(m => m.Ts >= start && m.Ts < end);
        if (!string.IsNullOrEmpty(device))
            query = query.Where(m => m.DeviceId == device);

        // Few thousand rows a month at most: grouping in memory keeps the time zone logic in one place.
        var stamps = await query.Select(m => m.Ts).ToListAsync(ct);
        return stamps
            .GroupBy(ts => DateOnly.FromDateTime(hive.Value.ToLocal(ts).DateTime))
            .OrderBy(g => g.Key)
            .Select(g => new MediaDayDto(g.Key, g.Count()))
            .ToList();
    }

    public Task<MediaItem?> FindAsync(string id, CancellationToken ct) =>
        db.Media.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<bool> SetPinnedAsync(string id, bool pinned, CancellationToken ct) =>
        await db.Media.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Pinned, pinned), ct) > 0;
}
