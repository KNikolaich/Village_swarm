using System.Globalization;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Modules.Media;
using Hive.Modules.Notify.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Hive.Modules.Notify;

/// <summary>
/// Sends the notifications table (spec 6.3 NotificationOutbox): retries with backoff while Telegram or the
/// internet is down, waits for photos, edits grouped alarms to "×N", repeats unacknowledged critical alarms.
/// </summary>
public sealed class NotificationOutbox(
    IServiceScopeFactory scopes,
    TelegramGateway telegram,
    IOptions<TelegramOptions> options,
    TimeProvider time,
    ILogger<NotificationOutbox> logger) : BackgroundService
{
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);
    private DateTimeOffset _nextCriticalCheck = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!telegram.Enabled)
        {
            logger.LogInformation("Telegram is not configured (Telegram:Token); notifications stay in the feed only");
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
                var store = scope.ServiceProvider.GetRequiredService<MediaStore>();
                await RunOnceAsync(db, store, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Notification outbox pass failed");
            }
        }
    }

    internal async Task RunOnceAsync(HiveDbContext db, MediaStore store, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (now >= _nextCriticalCheck)
        {
            await RepeatCriticalAsync(db, now, ct);
            _nextCriticalCheck = now.AddMinutes(1);
        }

        foreach (var n in await db.Notifications.Where(n => n.EditPending && n.Status == NotificationStatus.Sent).Take(20).ToListAsync(ct))
            await EditGroupedAsync(n, ct);

        var due = await db.Notifications
            .Where(n => n.Status == NotificationStatus.Pending && n.NextAttemptAt <= now)
            .OrderBy(n => n.Id)
            .Take(20)
            .ToListAsync(ct);
        foreach (var n in due)
        {
            if (now - n.CreatedAt > GiveUpAfter)
            {
                n.Status = NotificationStatus.Failed;
                n.Error ??= "gave up after 24 h";
                continue;
            }
            await SendAsync(db, store, n, now, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SendAsync(HiveDbContext db, MediaStore store, Notification n, DateTimeOffset now, CancellationToken ct)
    {
        var chat = long.Parse(n.Target, CultureInfo.InvariantCulture);
        try
        {
            switch (n.Kind)
            {
                case NotificationKind.Event:
                case NotificationKind.Text:
                    var message = await telegram.CallAsync(bot => bot.SendMessage(chat, n.Text, ParseMode.Html,
                        replyMarkup: n.Kind == NotificationKind.Event ? EventButtons(n) : null,
                        disableNotification: n.Silent, cancellationToken: ct), ct);
                    MarkSent(n, message.MessageId, now);
                    break;

                case NotificationKind.EventPhotos:
                    await SendEventPhotosAsync(db, store, n, chat, now, ct);
                    break;

                case NotificationKind.Snapshot:
                    await SendSnapshotAsync(db, store, n, chat, now, ct);
                    break;
            }
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 429)
        {
            n.NextAttemptAt = now.AddSeconds(ex.Parameters?.RetryAfter ?? 5);
        }
        catch (ApiRequestException ex)
        {
            // 400/403: bad message or the user blocked the bot. Retrying will not help.
            n.Status = NotificationStatus.Failed;
            n.Error = $"{ex.ErrorCode}: {ex.Message}";
            logger.LogWarning("Telegram rejected notification {Id} to {Chat}: {Error}", n.Id, n.Target, n.Error);
        }
        catch (Exception ex) when (TelegramGateway.IsNetworkError(ex) && !ct.IsCancellationRequested)
        {
            n.Attempts++;
            n.Error = ex.Message;
            n.NextAttemptAt = now.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(n.Attempts, 9))));
        }
    }

    private static InlineKeyboardMarkup EventButtons(Notification n) => new(
    [
        [
            InlineKeyboardButton.WithCallbackData("✅ Принял", $"ack:{n.EventId}"),
            InlineKeyboardButton.WithCallbackData("📷 Ещё фото", $"photo:{n.DeviceIds}"),
            InlineKeyboardButton.WithCallbackData("🔕 1 ч тишины", "mute:1"),
        ],
    ]);

    private async Task SendEventPhotosAsync(HiveDbContext db, MediaStore store, Notification n, long chat, DateTimeOffset now, CancellationToken ct)
    {
        // The text goes first; photos are a reply to it.
        var parent = await db.Notifications.AsNoTracking()
            .Where(x => x.Kind == NotificationKind.Event && x.EventId == n.EventId && x.Target == n.Target)
            .Select(x => new { x.Status, x.MessageId })
            .FirstOrDefaultAsync(ct);
        if (parent is null || parent.Status == NotificationStatus.Failed)
        {
            n.Status = NotificationStatus.Skipped;
            return;
        }
        if (parent.MessageId is null)
        {
            n.NextAttemptAt = now.AddSeconds(1);
            return;
        }

        var photos = await db.Media.AsNoTracking().Where(m => m.EventId == n.EventId).OrderBy(m => m.Id).Take(3).ToListAsync(ct);
        if (photos.Count < Math.Min(3, n.ExpectedPhotos) && now < n.WaitUntil)
        {
            n.NextAttemptAt = now.AddSeconds(1);
            return;
        }
        if (photos.Count == 0)
        {
            n.Status = NotificationStatus.Skipped;
            n.Error = "no photos arrived";
            return;
        }
        var messages = await SendAlbumAsync(store, chat, photos, (int)parent.MessageId, silent: true, ct);
        MarkSent(n, messages[0].MessageId, now);
    }

    private async Task SendSnapshotAsync(HiveDbContext db, MediaStore store, Notification n, long chat, DateTimeOffset now, CancellationToken ct)
    {
        var devices = (n.DeviceIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var photos = await db.Media.AsNoTracking()
            .Where(m => devices.Contains(m.DeviceId) && m.Kind == MediaKind.Snapshot && m.ReceivedAt >= n.CreatedAt)
            .OrderBy(m => m.DeviceId).ThenByDescending(m => m.Ts)
            .ToListAsync(ct);
        // Newest snapshot per camera, at most 10 (Telegram album limit).
        var latest = photos.GroupBy(m => m.DeviceId).Select(g => g.First()).Take(10).ToList();
        if (latest.Count < n.ExpectedPhotos && now < n.WaitUntil)
        {
            n.NextAttemptAt = now.AddSeconds(1);
            return;
        }
        if (latest.Count == 0)
        {
            var message = await telegram.CallAsync(bot => bot.SendMessage(chat, "📷 Камеры не прислали снимок вовремя.",
                replyParameters: n.ReplyToMessageId is { } r ? new ReplyParameters { MessageId = (int)r } : null, cancellationToken: ct), ct);
            MarkSent(n, message.MessageId, now);
            return;
        }
        var sent = await SendAlbumAsync(store, chat, latest, (int?)n.ReplyToMessageId, silent: false, ct);
        MarkSent(n, sent[0].MessageId, now);
    }

    private async Task<Message[]> SendAlbumAsync(MediaStore store, long chat, List<MediaItem> photos, int? replyTo, bool silent, CancellationToken ct)
    {
        // Streams are reopened per attempt: a failed endpoint may have consumed them.
        return await telegram.CallAsync(async bot =>
        {
            var streams = photos.Select(m => File.OpenRead(store.FullPath(m.Path))).ToList();
            try
            {
                var album = photos.Select((m, i) => new InputMediaPhoto(InputFile.FromStream(streams[i], $"{m.Id}.jpg"))).ToList();
                return await bot.SendMediaGroup(chat, album,
                    replyParameters: replyTo is { } r ? new ReplyParameters { MessageId = r, AllowSendingWithoutReply = true } : null,
                    disableNotification: silent, cancellationToken: ct);
            }
            finally
            {
                foreach (var s in streams)
                    await s.DisposeAsync();
            }
        }, ct);
    }

    private async Task EditGroupedAsync(Notification n, CancellationToken ct)
    {
        var chat = long.Parse(n.Target, CultureInfo.InvariantCulture);
        try
        {
            await telegram.CallAsync(bot => bot.EditMessageText(chat, (int)n.MessageId!, $"{n.Text}\n<b>×{n.RepeatCount}</b> подряд",
                ParseMode.Html, replyMarkup: EventButtons(n), cancellationToken: ct), ct);
            n.EditPending = false;
        }
        catch (ApiRequestException ex)
        {
            n.EditPending = false; // e.g. "message is not modified" or deleted by the user
            logger.LogDebug("Edit of notification {Id} failed: {Error}", n.Id, ex.Message);
        }
        catch (Exception ex) when (TelegramGateway.IsNetworkError(ex) && !ct.IsCancellationRequested)
        {
            // keep EditPending, try on the next pass
        }
    }

    /// <summary>Unacknowledged critical events get a reminder every CriticalRepeatMin minutes (spec 6.7).</summary>
    private async Task RepeatCriticalAsync(HiveDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var since = now.AddHours(-2);
        var critical = await db.Events.AsNoTracking()
            .Where(e => e.Severity == EventSeverity.Critical && e.AcknowledgedAt == null && e.ReceivedAt >= since)
            .ToListAsync(ct);
        if (critical.Count == 0)
            return;
        var every = TimeSpan.FromMinutes(options.Value.CriticalRepeatMin);
        foreach (var e in critical)
        {
            var sent = await db.Notifications.AsNoTracking()
                .Where(n => n.EventId == e.Id && n.Kind == NotificationKind.Event)
                .Select(n => new { n.Id, n.Target, n.CreatedAt, n.Text })
                .ToListAsync(ct);
            var lastByChat = sent.GroupBy(n => n.Target).Select(g => new
            {
                Target = g.Key,
                Last = g.Max(n => n.CreatedAt),
                Text = g.MinBy(n => n.Id)!.Text,
            });
            foreach (var chat in lastByChat.Where(c => now - c.Last >= every))
            {
                db.Notifications.Add(new Notification
                {
                    Target = chat.Target,
                    Kind = NotificationKind.Event,
                    EventId = e.Id,
                    DeviceIds = e.DeviceId,
                    Text = "🔁 <b>Не подтверждено</b>\n" + chat.Text,
                    CreatedAt = now,
                    NextAttemptAt = now,
                });
            }
        }
    }

    private static void MarkSent(Notification n, int messageId, DateTimeOffset now)
    {
        n.Status = NotificationStatus.Sent;
        n.MessageId = messageId;
        n.SentAt = now;
        n.Error = null;
    }
}
