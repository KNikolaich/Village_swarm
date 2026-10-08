using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Hive.Modules.Devices;
using Hive.Modules.Events;
using Hive.Modules.Telemetry;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using static Hive.Modules.Notify.Texts;

namespace Hive.Modules.Notify.Telegram;

/// <summary>Bot commands and inline buttons (spec 9.3, 9.4). Only linked chats are served; others are audited.</summary>
public sealed class BotCommands(
    HiveDbContext db,
    TelegramGateway telegram,
    UserManager<HiveUser> users,
    ArmService arm,
    CommandService commands,
    EventQueries events,
    TelemetryQueries telemetry,
    IOptions<HiveOptions> hive,
    IOptions<TelegramOptions> options,
    TimeProvider time)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private sealed record Caller(TgLink Link, HiveUser User, bool CanControl)
    {
        public string Actor => $"tg:{Link.Username ?? Link.ChatId.ToString(CultureInfo.InvariantCulture)}";
    }

    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        if (update.Message is { Text: { } text } message && text.StartsWith('/'))
            await OnCommandAsync(message, text, ct);
        else if (update.CallbackQuery is { } callback)
            await OnCallbackAsync(callback, ct);
    }

    private async Task OnCommandAsync(Message message, string text, CancellationToken ct)
    {
        var chat = message.Chat.Id;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].Split('@')[0].ToLowerInvariant();
        var args = parts.Skip(1).ToArray();

        if (command == "/link")
        {
            await LinkAsync(message, args.FirstOrDefault(), ct);
            return;
        }

        var caller = await CallerAsync(chat, ct);
        if (caller is null)
        {
            Audit.Record(db, time.GetUtcNow(), $"tg:{message.From?.Username ?? chat.ToString(CultureInfo.InvariantCulture)}", "tg_unlinked_command", chat.ToString(CultureInfo.InvariantCulture), new { text });
            await db.SaveChangesAsync(ct);
            if (command is "/start" or "/help")
                await ReplyAsync(chat, NotLinked, ct);
            return;
        }

        switch (command)
        {
            case "/start" or "/help":
                await ReplyAsync(chat, Help, ct);
                break;
            case "/status":
                await ReplyAsync(chat, await StatusAsync(ct), ct);
                break;
            case "/events":
                var n = args.Length > 0 && int.TryParse(args[0], out var parsed) ? Math.Clamp(parsed, 1, 20) : 5;
                await ReplyAsync(chat, await EventsAsync(n, ct), ct);
                break;
            case "/temp":
                await ReplyAsync(chat, await TemperaturesAsync(ct), ct);
                break;
            case "/devices":
                await ReplyAsync(chat, await DevicesAsync(ct), ct);
                break;
            case "/mute":
                var hours = args.Length > 0 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? Math.Clamp(h, 0, 72) : 1;
                await MuteAsync(caller, hours, ct);
                await ReplyAsync(chat, hours > 0 ? $"🔕 Предупреждения не придут {hours.ToString("0.#", Ru)} ч. Тревоги придут как обычно." : "🔔 Тишина выключена.", ct);
                break;
            case "/arm" when Require(caller):
                var armed = await arm.SetAsync(true, caller.Actor, ct);
                await ReplyAsync(chat, $"🛡 Охрана включена. Камер: {armed.Cameras}.", ct);
                break;
            case "/disarm" when Require(caller):
                // Dangerous: confirm with a button (spec 9.4).
                await ReplyAsync(chat, "Снять охрану?", ct, new InlineKeyboardMarkup(
                [[InlineKeyboardButton.WithCallbackData("Да, снять", "disarm:yes"), InlineKeyboardButton.WithCallbackData("Отмена", "disarm:no")]]));
                break;
            case "/photo" when Require(caller):
                await SnapshotAsync(caller, chat, args.FirstOrDefault(), message.MessageId, ct);
                break;
            case "/arm" or "/disarm" or "/photo":
                await ReplyAsync(chat, "⛔ Недостаточно прав: у вас роль только для просмотра.", ct);
                break;
            default:
                await ReplyAsync(chat, "Не знаю такой команды. /help — список.", ct);
                break;
        }
    }

    private async Task OnCallbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        var chat = callback.Message?.Chat.Id ?? callback.From.Id;
        var caller = await CallerAsync(chat, ct);
        string answer;
        if (caller is null)
            answer = "Чат не привязан";
        else
        {
            var data = callback.Data ?? "";
            var (action, arg) = data.Split(':', 2) is [var a, var b] ? (a, b) : (data, "");
            switch (action)
            {
                case "ack" when Require(caller):
                    answer = await events.AcknowledgeAsync(arg, caller.Actor, ct) is { } e
                        ? e.AcknowledgedBy == caller.Actor ? "Принято" : $"Уже подтвердил {e.AcknowledgedBy}"
                        : "Событие не найдено";
                    break;
                case "photo" when Require(caller):
                    await SnapshotAsync(caller, chat, arg, callback.Message?.MessageId, ct);
                    answer = "Снимаю…";
                    break;
                case "mute":
                    await MuteAsync(caller, double.TryParse(arg, CultureInfo.InvariantCulture, out var h) ? h : 1, ct);
                    answer = "Предупреждения на паузе";
                    break;
                case "disarm" when Require(caller):
                    if (arg == "yes")
                    {
                        await arm.SetAsync(false, caller.Actor, ct);
                        answer = "Охрана снята";
                    }
                    else
                    {
                        answer = "Отменено";
                    }
                    if (callback.Message is { } m)
                        await telegram.CallAsync(bot => bot.EditMessageText(chat, m.MessageId, arg == "yes" ? "🔓 Охрана снята." : "Охрана не тронута.", cancellationToken: ct), ct);
                    break;
                default:
                    answer = "⛔ Недостаточно прав";
                    break;
            }
        }
        await telegram.CallAsync(bot => bot.AnswerCallbackQuery(callback.Id, answer, cancellationToken: ct), ct);
    }

    private async Task LinkAsync(Message message, string? code, CancellationToken ct)
    {
        var chat = message.Chat.Id;
        var now = time.GetUtcNow();
        var entry = code is null ? null : await db.TgLinkCodes.FirstOrDefaultAsync(c => c.Code == code.ToUpperInvariant() && c.UsedAt == null && c.ExpiresAt > now, ct);
        if (entry is null)
        {
            Audit.Record(db, now, $"tg:{message.From?.Username ?? chat.ToString(CultureInfo.InvariantCulture)}", "tg_link_failed", chat.ToString(CultureInfo.InvariantCulture));
            await db.SaveChangesAsync(ct);
            await ReplyAsync(chat, "Код не подошёл или истёк. Получите новый в веб-интерфейсе.", ct);
            return;
        }
        entry.UsedAt = now;
        var link = await db.TgLinks.FirstOrDefaultAsync(l => l.ChatId == chat, ct) ?? db.TgLinks.Add(new TgLink { ChatId = chat }).Entity;
        link.UserId = entry.UserId;
        link.Username = message.From?.Username ?? message.Chat.Title;
        link.LinkedAt = now;
        var user = await users.FindByIdAsync(entry.UserId.ToString(CultureInfo.InvariantCulture));
        Audit.Record(db, now, $"user:{user?.UserName}", "tg_linked", chat.ToString(CultureInfo.InvariantCulture), new { link.Username });
        await db.SaveChangesAsync(ct);
        await ReplyAsync(chat, $"✅ Чат привязан к пользователю <b>{H(user?.UserName)}</b>. Сюда будут приходить тревоги.\n\n{Help}", ct);
    }

    private async Task<string> StatusAsync(CancellationToken ct)
    {
        var mode = await arm.GetAsync(ct);
        var devices = await db.Devices.AsNoTracking().Where(d => d.Status != DeviceStatus.Disabled).ToListAsync(ct);
        var online = devices.Count(d => d.Status == DeviceStatus.Online);
        var since = time.GetUtcNow().AddDays(-1);
        var unacked = await db.Events.CountAsync(e => e.Severity >= EventSeverity.Alarm && e.AcknowledgedAt == null && e.Ts >= since, ct);
        var sb = new StringBuilder();
        sb.AppendLine(mode.Armed ? "🛡 Охрана: <b>включена</b>" : "🔓 Охрана: <b>снята</b>");
        sb.AppendLine($"🐝 Рой: {online}/{devices.Count} на связи");
        sb.AppendLine(await TemperaturesAsync(ct));
        sb.Append(unacked > 0 ? $"🚨 Неподтверждённых тревог за сутки: {unacked}" : "✅ Неподтверждённых тревог нет");
        return sb.ToString();
    }

    private async Task<string> TemperaturesAsync(CancellationToken ct)
    {
        var latest = await telemetry.LatestAsync(null, ct);
        var names = await db.Devices.AsNoTracking().ToDictionaryAsync(d => d.DeviceId, d => d.Name, ct);
        var temps = latest.Where(v => v.Metric == "temperature").OrderBy(v => v.DeviceId).ToList();
        if (temps.Count == 0)
            return "🌡 Нет свежих показаний температуры";
        return "🌡 " + string.Join(", ", temps.Select(v => $"{H(names.GetValueOrDefault(v.DeviceId, v.DeviceId))}: {Temperature(v.Value)}"));
    }

    private async Task<string> EventsAsync(int count, CancellationToken ct)
    {
        var page = await events.ListAsync(new EventFilter(null, null, null, null, null, null, null, count), ct);
        if (page.Items.Count == 0)
            return "Событий нет.";
        return string.Join('\n', page.Items.Select(e =>
            $"{Icon(Enum.Parse<EventSeverity>(e.Severity, true))} {hive.Value.ToLocal(e.Ts).ToString("dd.MM HH:mm", Ru)} {H(EventType(e.Type))} · {H(e.Zone ?? e.DeviceName ?? e.DeviceId)}"));
    }

    private async Task<string> DevicesAsync(CancellationToken ct)
    {
        var offline = await db.Devices.AsNoTracking().Where(d => d.Status == DeviceStatus.Offline).OrderBy(d => d.DeviceId).ToListAsync(ct);
        var weak = await db.Devices.AsNoTracking().Where(d => d.Status == DeviceStatus.Online && d.Rssi < -80).ToListAsync(ct);
        if (offline.Count == 0 && weak.Count == 0)
            return "✅ Все шершни на связи.";
        var sb = new StringBuilder();
        foreach (var d in offline)
            sb.AppendLine($"📴 {H(d.Name)} — не на связи с {(d.LastSeenAt is { } t ? hive.Value.ToLocal(t).ToString("dd.MM HH:mm", Ru) : "—")}");
        foreach (var d in weak)
            sb.AppendLine($"📶 {H(d.Name)} — слабый сигнал {d.Rssi} dBm");
        return sb.ToString().TrimEnd();
    }

    private async Task SnapshotAsync(Caller caller, long chat, string? target, int? replyTo, CancellationToken ct)
    {
        var cameras = await db.Devices.AsNoTracking()
            .Where(d => d.TypeCode == "guard-cam" && d.Status == DeviceStatus.Online)
            .Select(d => new { d.DeviceId, d.Name })
            .ToListAsync(ct);
        if (!string.IsNullOrEmpty(target))
            cameras = cameras.Where(c => c.DeviceId == target || c.Name.Equals(target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cameras.Count == 0)
        {
            await ReplyAsync(chat, string.IsNullOrEmpty(target) ? "📷 Нет камер на связи." : $"📷 Камера «{H(target)}» не на связи или не найдена.", ct);
            return;
        }

        var now = time.GetUtcNow();
        var sentTo = new List<string>();
        foreach (var camera in cameras)
            if ((await commands.SendAsync(camera.DeviceId, "snapshot", new JsonObject { ["count"] = 1 }, caller.Actor, 30, ct)).Status == SendStatus.Sent)
                sentTo.Add(camera.DeviceId);

        var ack = await ReplyAsync(chat, $"📷 Снимаю: {H(string.Join(", ", cameras.Where(c => sentTo.Contains(c.DeviceId)).Select(c => c.Name)))}…", ct, replyTo: replyTo);
        db.Notifications.Add(new Notification
        {
            Target = chat.ToString(CultureInfo.InvariantCulture),
            Kind = NotificationKind.Snapshot,
            DeviceIds = string.Join(',', sentTo),
            ExpectedPhotos = sentTo.Count,
            Text = "",
            CreatedAt = now,
            NextAttemptAt = now.AddSeconds(2),
            WaitUntil = now.AddSeconds(options.Value.PhotoWaitS + 10),
            ReplyToMessageId = ack.MessageId,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task MuteAsync(Caller caller, double hours, CancellationToken ct)
    {
        var link = await db.TgLinks.FirstAsync(l => l.Id == caller.Link.Id, ct);
        link.MutedUntil = hours > 0 ? time.GetUtcNow().AddHours(hours) : null;
        await db.SaveChangesAsync(ct);
    }

    private async Task<Caller?> CallerAsync(long chat, CancellationToken ct)
    {
        var link = await db.TgLinks.AsNoTracking().FirstOrDefaultAsync(l => l.ChatId == chat, ct);
        if (link is null)
            return null;
        var user = await users.FindByIdAsync(link.UserId.ToString(CultureInfo.InvariantCulture));
        if (user is null || await users.IsLockedOutAsync(user))
            return null;
        var roles = await users.GetRolesAsync(user);
        return new Caller(link, user, roles.Contains(HiveRoles.Admin) || roles.Contains(HiveRoles.Member));
    }

    private static bool Require(Caller caller) => caller.CanControl;

    private Task<Message> ReplyAsync(long chat, string text, CancellationToken ct, InlineKeyboardMarkup? markup = null, int? replyTo = null) =>
        telegram.CallAsync(bot => bot.SendMessage(chat, text, ParseMode.Html, replyMarkup: markup,
            replyParameters: replyTo is { } r ? new ReplyParameters { MessageId = r, AllowSendingWithoutReply = true } : null,
            cancellationToken: ct), ct);
}
