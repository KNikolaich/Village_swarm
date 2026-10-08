using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Modules.Notify.Telegram;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Notify;

/// <summary>
/// Turns ingested events into outbox rows per linked chat (spec 6.7): info only in the feed, warn silent,
/// alarm/critical with sound and photos; the same alarm again within the window is grouped ("×N").
/// </summary>
public sealed class NotificationPlanner(IServiceScopeFactory scopes, IOptions<TelegramOptions> options, IOptions<HiveOptions> hive, TimeProvider time)
    : IIngestListener
{
    public async Task OnIngestedAsync(IReadOnlyList<IngestItem> items, CancellationToken ct)
    {
        var events = items.OfType<DeviceEventReceived>().Where(e => e.Severity >= EventSeverity.Warn).ToList();
        if (events.Count == 0 || !options.Value.Enabled)
            return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var links = await db.TgLinks.AsNoTracking().ToListAsync(ct);
        if (links.Count == 0)
            return;

        var now = time.GetUtcNow();
        foreach (var received in events)
        {
            var e = await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == received.MessageId, ct);
            if (e is null)
                continue;
            await PlanAsync(db, e, links, now, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    internal async Task PlanAsync(HiveDbContext db, DeviceEvent e, List<TgLink> links, DateTimeOffset now, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().Include(d => d.Zone).FirstOrDefaultAsync(d => d.DeviceId == e.DeviceId, ct);
        var place = device?.Zone?.Name ?? device?.Name ?? e.DeviceId;
        var text = Texts.Event(e, place, hive.Value.ToLocal(e.Ts));
        var dedupeKey = $"{e.DeviceId}:{e.Type}";
        var windowStart = now.AddSeconds(-options.Value.GroupWindowS);
        var expectedPhotos = e.Payload.RootElement.TryGetProperty("photos", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Array
            ? p.GetArrayLength()
            : 0;

        foreach (var link in links)
        {
            if (e.Severity < link.NotifyLevel)
                continue;
            if (e.Severity == EventSeverity.Warn && link.MutedUntil > now)
                continue;
            var target = link.ChatId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var earlier = await db.Notifications
                .Where(n => n.Target == target && n.DedupeKey == dedupeKey && n.Kind == NotificationKind.Event
                            && n.CreatedAt >= windowStart && n.Status != NotificationStatus.Failed)
                .OrderBy(n => n.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (earlier is not null && e.Severity != EventSeverity.Critical)
            {
                earlier.RepeatCount++;
                earlier.EditPending = true;
                continue;
            }

            db.Notifications.Add(new Notification
            {
                Target = target,
                Kind = NotificationKind.Event,
                EventId = e.Id,
                DeviceIds = e.DeviceId,
                Text = text,
                Silent = e.Severity == EventSeverity.Warn,
                DedupeKey = dedupeKey,
                CreatedAt = now,
                NextAttemptAt = now,
            });
            if (expectedPhotos > 0 && e.Severity >= EventSeverity.Alarm)
            {
                db.Notifications.Add(new Notification
                {
                    Target = target,
                    Kind = NotificationKind.EventPhotos,
                    EventId = e.Id,
                    DeviceIds = e.DeviceId,
                    Text = "",
                    ExpectedPhotos = expectedPhotos,
                    CreatedAt = now,
                    NextAttemptAt = now.AddSeconds(1),
                    WaitUntil = now.AddSeconds(options.Value.PhotoWaitS),
                });
            }
        }
    }
}
