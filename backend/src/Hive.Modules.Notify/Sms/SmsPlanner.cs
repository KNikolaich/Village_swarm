using System.Globalization;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Notify.Sms;

/// <summary>Short Russian texts for SMS: plain characters, no HTML, no emoji (a pair of them costs two characters of the 70).</summary>
public static class SmsTexts
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>"Улей: Протечка: Подвал, 14:32".</summary>
    public static string Event(DeviceEvent e, string place, DateTimeOffset localTs) =>
        $"Улей: {Texts.EventType(e.Type)}: {place}, {localTs.ToString("HH:mm", Ru)}";
}

/// <summary>
/// Turns critical events into outbox rows with the channel "sms", one per phone number (docs/sms-keenetic.md). Only critical:
/// an SMS is paid and wakes people up. The same event of the same device inside the window is not sent again, and the node
/// sends at most <see cref="SmsOptions.MaxPerHour"/> per hour; what exceeds it is kept as skipped so the log shows it.
/// </summary>
public sealed class SmsPlanner(IServiceScopeFactory scopes, IOptions<SmsOptions> options, IOptions<HiveOptions> hive, NodeState node, TimeProvider time)
    : IIngestListener
{
    public const string Channel = "sms";

    public async Task OnIngestedAsync(IReadOnlyList<IngestItem> items, CancellationToken ct)
    {
        if (!options.Value.Enabled)
            return;
        var events = items.OfType<DeviceEventReceived>().Where(e => e.Severity == EventSeverity.Critical && node.Notifies(e.DeviceId)).ToList();
        if (events.Count == 0)
            return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var now = time.GetUtcNow();
        foreach (var received in events)
        {
            var e = await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == received.MessageId, ct);
            if (e is not null)
                await PlanAsync(db, e, now, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    internal async Task PlanAsync(HiveDbContext db, DeviceEvent e, DateTimeOffset now, CancellationToken ct)
    {
        var o = options.Value;
        var device = await db.Devices.AsNoTracking().Include(d => d.Zone).FirstOrDefaultAsync(d => d.DeviceId == e.DeviceId, ct);
        var place = device?.Zone?.Name ?? device?.Name ?? e.DeviceId;
        var text = SmsTexts.Event(e, place, hive.Value.ToLocal(e.Ts));
        var dedupeKey = $"{e.DeviceId}:{e.Type}";
        var windowStart = now.AddMinutes(-o.GroupWindowMin);
        var hourStart = now.AddHours(-1);
        var inLastHour = await db.Notifications.CountAsync(n => n.Channel == Channel && n.CreatedAt >= hourStart && n.Status != NotificationStatus.Skipped, ct);

        foreach (var number in o.Numbers)
        {
            var again = await db.Notifications.AnyAsync(n => n.Channel == Channel && n.Target == number && n.DedupeKey == dedupeKey
                && n.CreatedAt >= windowStart && n.Status != NotificationStatus.Failed, ct);
            if (again)
                continue;

            var overLimit = inLastHour >= o.MaxPerHour;
            db.Notifications.Add(new Notification
            {
                Channel = Channel,
                Target = number,
                Kind = NotificationKind.Text,
                EventId = e.Id,
                DeviceIds = e.DeviceId,
                Text = text,
                DedupeKey = dedupeKey,
                Status = overLimit ? NotificationStatus.Skipped : NotificationStatus.Pending,
                Error = overLimit ? $"limit of {o.MaxPerHour} SMS per hour" : null,
                CreatedAt = now,
                NextAttemptAt = now,
            });
            if (!overLimit)
                inLastHour++;
        }
    }
}
