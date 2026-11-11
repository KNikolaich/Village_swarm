using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Modules.Devices;
using Hive.Modules.Events;
using Hive.Modules.Media;
using Hive.Modules.Notes;
using Hive.Modules.Notify;
using Hive.Modules.Notify.Telegram;
using Hive.Modules.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using static Hive.Modules.Notify.Texts;

namespace Hive.Modules.Assistant;

/// <summary>What the assistant can do for the person: the tools the model calls, each ending in a proposal to confirm.</summary>
public sealed partial class TelegramAssistant
{
    private async Task<string> ZoneStatusAsync(long chat, string? query, int replyTo, CancellationToken ct)
    {
        var zones = await db.Zones.AsNoTracking().ToListAsync(ct);
        var zone = Pick(zones, z => z.Name, query);
        if (zone is null)
        {
            await SendAsync(chat, $"🤖 Не нашёл зону «{H(query)}». Зоны: {H(string.Join(", ", zones.Select(z => z.Name)))}.", ct, replyTo: replyTo);
            return "[зона не найдена]";
        }

        var devices = await db.Devices.AsNoTracking().Where(d => d.ZoneId == zone.Id && d.Status != DeviceStatus.Disabled).OrderBy(d => d.Sort).ThenBy(d => d.Name).ToListAsync(ct);
        var ids = devices.Select(d => d.DeviceId).ToHashSet();
        var latest = (await telemetry.LatestAsync(null, ct)).Where(v => ids.Contains(v.DeviceId)).ToLookup(v => v.DeviceId);
        var recent = await events.ListAsync(new EventFilter(null, null, zone.Id, null, null, null, null, 5), ct);

        var sb = new StringBuilder();
        sb.AppendLine($"📍 <b>{H(zone.Name)}</b>");
        if (devices.Count == 0)
            sb.AppendLine("В зоне нет устройств.");
        else
            sb.AppendLine($"🐝 Устройств: {devices.Count}, на связи {devices.Count(d => d.Status == DeviceStatus.Online)}");
        foreach (var d in devices)
        {
            var line = $"• {H(d.Name)} — " + (d.Status == DeviceStatus.Online
                ? "на связи"
                : $"нет связи{(d.LastSeenAt is { } seen ? " с " + hive.Value.ToLocal(seen).ToString("dd.MM HH:mm", Ru) : "")}");
            var values = latest[d.DeviceId].OrderBy(x => x.Metric).Select(x => Reading(x.Metric, x.Value)).ToList();
            if (values.Count > 0)
                line += " · " + string.Join(" · ", values);
            sb.AppendLine(line);
        }
        if (recent.Items.Count > 0)
        {
            sb.AppendLine("Последние события:");
            foreach (var e in recent.Items)
                sb.AppendLine($"{Icon(Enum.Parse<EventSeverity>(e.Severity, true))} {hive.Value.ToLocal(e.Ts).ToString("dd.MM HH:mm", Ru)} {H(EventType(e.Type))} · {H(e.DeviceName ?? e.DeviceId)}");
        }

        var cameras = devices.Where(d => Cameras.Types.Contains(d.TypeCode)).ToList();
        var online = cameras.Where(d => d.Status == DeviceStatus.Online).ToList();
        var markup = online.Count > 0
            ? new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("📷 Снять свежие", $"{Prefix}snapzone:{zone.Id}"))
            : null;
        var sent = await SendAsync(chat, sb.ToString().TrimEnd(), ct, markup, replyTo);

        // The last frame of each camera in the zone (a week back at most), captioned with when it was taken.
        var since = time.GetUtcNow().AddDays(-7);
        var cameraIds = cameras.Select(c => c.DeviceId).ToList();
        var photos = (await db.Media.AsNoTracking().Where(m => cameraIds.Contains(m.DeviceId) && m.Ts >= since).ToListAsync(ct))
            .GroupBy(m => m.DeviceId).Select(g => g.OrderByDescending(m => m.Ts).First())
            .Where(m => File.Exists(media.FullPath(m.Path))).Take(10).ToList();
        if (photos.Count > 0)
        {
            var names = cameras.ToDictionary(c => c.DeviceId, c => c.Name);
            await outbox.SendAlbumAsync(media, chat, photos, sent.MessageId, silent: true, ct,
                caption: m => $"{H(names.GetValueOrDefault(m.DeviceId, m.DeviceId))}, {hive.Value.ToLocal(m.Ts).ToString("d MMMM HH:mm", Ru)}");
        }
        return $"[показал статус зоны {zone.Name}]";
    }

    private async Task<string> ProposeGuardAsync(long chat, string? mode, int replyTo, CancellationToken ct)
    {
        mode = mode is "armed" ? ArmService.ArmedMode : mode is "arriving" ? ArmService.Arriving : mode is "disarmed" ? ArmService.Disarmed : null;
        if (mode is null)
        {
            await SendAsync(chat, "🤖 Не понял, какой режим охраны нужен.", ct, replyTo: replyTo);
            return "[режим не понят]";
        }
        var current = await arm.GetAsync(ct);
        if (current.Mode == mode)
        {
            await SendAsync(chat, $"Уже так. {BotCommands.GuardLine(mode)}", ct, replyTo: replyTo);
            return "[режим уже такой]";
        }
        var text = mode switch
        {
            ArmService.ArmedMode => $"🛡 Поставить на охрану? Камер: {current.Cameras}.",
            ArmService.Arriving => "🚗 Включить подготовку к приезду? Камеры остаются на охране, движение придёт как «едут хозяева».",
            _ => "🔓 Снять охрану?",
        };
        await ProposeAsync(chat, AssistantTools.SetGuard, new JsonObject { ["mode"] = mode }, text, replyTo, ct);
        return $"[предложил режим охраны {mode}, ждёт подтверждения]";
    }

    private async Task<string> ProposePhotosAsync(long chat, JsonObject args, int replyTo, CancellationToken ct)
    {
        var cameras = await db.Devices.AsNoTracking().Include(d => d.Zone)
            .Where(d => Cameras.Types.Contains(d.TypeCode) && d.Status == DeviceStatus.Online).OrderBy(d => d.Name).ToListAsync(ct);
        var zone = Str(args, "zone");
        var place = Str(args, "place");
        var wanted = args["cameras"] is JsonArray list ? list.Select(x => x is JsonValue v && v.TryGetValue<string>(out var n) ? n : null).OfType<string>().ToList() : [];
        var scope = "все камеры";
        if (wanted.Count > 0)
        {
            cameras = wanted.Select(w => PickDevice(cameras, w)).OfType<Device>().Distinct().ToList();
            scope = "выбранные камеры";
        }
        else if (!string.IsNullOrWhiteSpace(zone))
        {
            var zones = await db.Zones.AsNoTracking().ToListAsync(ct);
            var z = Pick(zones, x => x.Name, zone);
            cameras = z is null ? [] : cameras.Where(c => c.ZoneId == z.Id).ToList();
            scope = z is null ? $"зона «{zone}»" : $"зона «{z.Name}»";
        }
        else if (place is "indoor" or "outdoor")
        {
            var indoor = place == "indoor";
            cameras = cameras.Where(c => c.Zone is { } z && AssistantContext.Indoor(z.Kind) == indoor).ToList();
            scope = indoor ? "камеры в доме" : "камеры на улице";
        }
        if (cameras.Count == 0)
        {
            await SendAsync(chat, $"📷 Нет камер на связи ({H(scope)}).", ct, replyTo: replyTo);
            return "[камер нет]";
        }
        var text = $"📷 Сделать снимки сейчас? {H(string.Join(", ", cameras.Select(c => c.Name)))}.";
        await ProposeAsync(chat, AssistantTools.TakePhotos, new JsonObject { ["devices"] = new JsonArray(cameras.Select(c => (JsonNode)c.DeviceId).ToArray()) }, text, replyTo, ct);
        return $"[предложил снимки ({scope}), ждёт подтверждения]";
    }

    private async Task<string> ProposeMuteAsync(long chat, JsonObject args, int replyTo, CancellationToken ct)
    {
        var hours = args["hours"] is JsonValue v && v.TryGetValue<double>(out var h) ? Math.Clamp(h, 0, 72) : 1;
        var text = hours > 0
            ? $"🔕 Включить тишину на {hours.ToString("0.#", Ru)} ч? Предупреждения не придут, тревоги придут как обычно."
            : "🔔 Выключить тишину?";
        await ProposeAsync(chat, AssistantTools.Mute, new JsonObject { ["hours"] = hours }, text, replyTo, ct);
        return $"[предложил тишину на {hours} ч, ждёт подтверждения]";
    }

    private async Task<string> ProposeViewedAsync(long chat, string? device, int replyTo, CancellationToken ct)
    {
        Device? target = null;
        if (!string.IsNullOrWhiteSpace(device))
        {
            target = PickDevice(await db.Devices.AsNoTracking().ToListAsync(ct), device);
            if (target is null)
            {
                await SendAsync(chat, $"🤖 Не нашёл устройство «{H(device)}».", ct, replyTo: replyTo);
                return "[устройство не найдено]";
            }
        }
        var count = await db.Events.AsNoTracking().CountAsync(e => e.AcknowledgedAt == null && (target == null || e.DeviceId == target.DeviceId), ct);
        if (count == 0)
        {
            await SendAsync(chat, "👁 Непросмотренных событий нет.", ct, replyTo: replyTo);
            return "[нечего отмечать]";
        }
        var text = $"👁 Отметить просмотренными {count} событ.{(target is null ? "" : $" ({H(target.Name)})")}?";
        await ProposeAsync(chat, AssistantTools.MarkViewed, new JsonObject { ["device"] = target?.DeviceId }, text, replyTo, ct);
        return "[предложил отметить события, ждёт подтверждения]";
    }

    private async Task<string> ProposeRelayAsync(long chat, JsonObject args, int replyTo, CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().Where(d => d.Status != DeviceStatus.Disabled).ToListAsync(ct);
        var withRelays = devices.Where(d => AssistantContext.RelayChannels(d).Length > 0).ToList();
        var device = PickDevice(withRelays, Str(args, "device"));
        if (device is null)
        {
            await SendAsync(chat, $"🤖 Не нашёл устройство с реле «{H(Str(args, "device"))}».", ct, replyTo: replyTo);
            return "[реле не найдено]";
        }
        var channels = AssistantContext.RelayChannels(device);
        var wanted = Str(args, "channel");
        var channel = string.IsNullOrWhiteSpace(wanted) ? (channels.Length == 1 ? channels[0].Channel : null)
            : channels.Select(c => c.Channel).FirstOrDefault(c => NoteService.Normalize(c) == NoteService.Normalize(wanted));
        if (channel is null)
        {
            await SendAsync(chat, $"🤖 У «{H(device.Name)}» несколько реле: {H(string.Join(", ", channels.Select(c => c.Channel)))}. Какое?", ct, replyTo: replyTo);
            return "[канал не понят]";
        }
        if (device.Status != DeviceStatus.Online)
        {
            await SendAsync(chat, $"📴 «{H(device.Name)}» сейчас не на связи.", ct, replyTo: replyTo);
            return "[устройство не на связи]";
        }
        var on = args["on"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var minutes = args["minutes"] is JsonValue m && m.TryGetValue<int>(out var mm) ? Math.Clamp(mm, 1, 1440) : (int?)null;
        var what = channels.Length > 1 ? $"{device.Name} ({channel})" : device.Name;
        var text = on
            ? $"💡 Включить «{H(what)}»{(minutes is { } x ? $" на {x} мин" : "")}?"
            : $"⏻ Выключить «{H(what)}»?";
        var payload = new JsonObject { ["device"] = device.DeviceId, ["channel"] = channel, ["on"] = on };
        if (on && minutes is { } forMin)
            payload["minutes"] = forMin;
        await ProposeAsync(chat, AssistantTools.SwitchRelay, payload, text, replyTo, ct);
        return $"[предложил {(on ? "включить" : "выключить")} {what}, ждёт подтверждения]";
    }

    private async Task<string> ProposeIrAsync(long chat, string? deviceQuery, string? buttonQuery, int replyTo, CancellationToken ct)
    {
        var devices = (await db.Devices.AsNoTracking().Where(d => d.Status != DeviceStatus.Disabled).ToListAsync(ct))
            .Where(d => IrButtonService.Read(d.Config).Count > 0).ToList();
        var device = PickDevice(devices, deviceQuery);
        var buttons = device is null ? [] : IrButtonService.Read(device.Config).Keys.ToList();
        var button = buttonQuery is null ? null : Pick(buttons, x => x, buttonQuery);
        if (device is null || button is null)
        {
            await SendAsync(chat, device is null
                ? $"🤖 Не нашёл пульт «{H(deviceQuery)}»."
                : $"🤖 У «{H(device.Name)}» нет кнопки «{H(buttonQuery)}». Есть: {H(string.Join(", ", buttons))}.", ct, replyTo: replyTo);
            return "[ИК-кнопка не найдена]";
        }
        var text = $"📡 Нажать «{H(button)}» на «{H(device.Name)}»?";
        await ProposeAsync(chat, AssistantTools.PressIr, new JsonObject { ["device"] = device.DeviceId, ["button"] = button }, text, replyTo, ct);
        return $"[предложил ИК {button}, ждёт подтверждения]";
    }

    private async Task<string> ProposeNoteAsync(long chat, JsonObject args, int replyTo, CancellationToken ct)
    {
        var path = args["path"] is JsonArray list ? list.Select(x => x?.GetValue<string>()?.Trim()).OfType<string>().Where(x => x.Length > 0).ToList() : [];
        var body = Str(args, "text")?.Trim() ?? "";
        if (body.Length == 0)
        {
            await SendAsync(chat, "📝 Какой текст записать в заметку?", ct, replyTo: replyTo);
            return "[нет текста заметки]";
        }
        var title = Str(args, "title")?.Trim();
        if (string.IsNullOrEmpty(title))
            title = TitleFrom(body);

        NotePathMatch match;
        try
        {
            match = await notes.MatchPathAsync(path, ct);
        }
        catch (NoteMasterUnavailableException)
        {
            await SendAsync(chat, "📝 Заметки хранятся в городе, а связи с ним сейчас нет. Повторите позже.", ct, replyTo: replyTo);
            return "[город недоступен, заметка не записана]";
        }
        var where = path.Count == 0 ? "в корень" : string.Join(" → ", match.Existing.Select(e => e.Title).Concat(match.Missing));
        var sb = new StringBuilder($"📝 Добавить заметку <b>{H(where)}</b>?\n«{H(title)}»");
        if (body != title)
            sb.Append($"\n{H(body.Length > 600 ? body[..600] + "…" : body)}");
        if (match.Missing.Count > 0)
            sb.Append($"\n\nНовые ветки: {H(string.Join(", ", match.Missing))}.");
        var exact = new JsonArray(match.Existing.Select(e => (JsonNode)e.Title).Concat(match.Missing.Select(m => (JsonNode)m)).ToArray());
        await ProposeAsync(chat, AssistantTools.AddNote, new JsonObject { ["path"] = exact, ["title"] = title, ["text"] = body }, sb.ToString(), replyTo, ct);
        return $"[предложил заметку в {where}, ждёт подтверждения]";
    }

    private async Task ProposeAsync(long chat, string tool, JsonObject args, string text, int replyTo, CancellationToken ct)
    {
        var action = state.Propose(chat, tool, args, text, TimeSpan.FromMinutes(options.Value.ConfirmMinutes));
        await SendAsync(chat, text, ct, new InlineKeyboardMarkup(
        [[
            InlineKeyboardButton.WithCallbackData("✅ Выполнить", $"{Prefix}ok:{action.Id}"),
            InlineKeyboardButton.WithCallbackData("✖ Отмена", $"{Prefix}no:{action.Id}"),
        ]]), replyTo);
    }
}
