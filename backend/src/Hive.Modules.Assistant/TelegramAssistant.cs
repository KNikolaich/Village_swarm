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

/// <summary>
/// Requests in plain words over Telegram: the LLM picks a tool, the hive does the rest. Reading answers at once;
/// anything that changes the house or the notes waits for a button press (spec: confirmation for every action).
/// </summary>
public sealed class TelegramAssistant(
    HiveDbContext db,
    LlmClient llm,
    AssistantState state,
    BotCommands bot,
    TelegramGateway telegram,
    NotificationOutbox outbox,
    MediaStore media,
    ArmService arm,
    EventQueries events,
    TelemetryQueries telemetry,
    NoteService notes,
    IOptions<AssistantOptions> options,
    IOptions<HiveOptions> hive,
    TimeProvider time,
    ILogger<TelegramAssistant> logger) : ITelegramAssistant
{
    private const string Prefix = "as:";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public bool Handles(Update update) => options.Value.Enabled && (
        update.Message is { Text: { } text } && !text.StartsWith('/') && !BotCommands.MenuCommands.ContainsKey(text)
        || update.CallbackQuery?.Data?.StartsWith(Prefix, StringComparison.Ordinal) == true);

    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } callback)
            await OnButtonAsync(callback, ct);
        else if (update.Message is { Text: { } text } message)
            await OnTextAsync(message, text.Trim(), ct);
    }

    private async Task OnTextAsync(Message message, string text, CancellationToken ct)
    {
        var chat = message.Chat.Id;
        var caller = await bot.CallerForAsync(chat, ct);
        if (caller is null)
        {
            Audit.Record(db, time.GetUtcNow(), $"tg:{message.From?.Username ?? chat.ToString(CultureInfo.InvariantCulture)}", "tg_unlinked_command", chat.ToString(CultureInfo.InvariantCulture), new { text });
            await db.SaveChangesAsync(ct);
            return;
        }
        if (text.Length == 0)
            return;
        if (!state.TryCount(options.Value.MaxRequestsPerDay))
        {
            await SendAsync(chat, "🤖 На сегодня лимит запросов к помощнику исчерпан. Команды /help работают как обычно.", ct);
            return;
        }

        try
        {
            await telegram.CallAsync(b => b.SendChatAction(chat, ChatAction.Typing, cancellationToken: ct), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("Typing action failed: {Error}", ex.Message); // cosmetic only, the request goes on
        }

        LlmReply reply;
        try
        {
            reply = await llm.CompleteAsync(await MessagesAsync(chat, text, ct), AssistantTools.Definitions(), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Assistant LLM call failed: {Error}", ex.Message);
            await SendAsync(chat, "🤖 Не получилось разобрать запрос: помощник сейчас недоступен. Команды /help работают как обычно.", ct);
            return;
        }

        Audit.Record(db, time.GetUtcNow(), caller.Actor, "assistant_request", null, new
        {
            text = text.Length > 500 ? text[..500] : text,
            tools = reply.ToolCalls.Select(c => c.Name).ToArray(),
            promptTokens = reply.PromptTokens,
            completionTokens = reply.CompletionTokens,
        });
        await db.SaveChangesAsync(ct);

        var done = new List<string>();
        if (reply.ToolCalls.Count == 0)
        {
            var answer = string.IsNullOrWhiteSpace(reply.Content) ? "🤖 Не понял запрос. Попробуйте сказать иначе или посмотрите /help." : reply.Content.Trim();
            await SendAsync(chat, H(answer), ct, replyTo: message.MessageId);
            done.Add(answer);
        }
        foreach (var call in reply.ToolCalls.Take(4))
            done.Add(await RunToolAsync(caller, message.MessageId, call, ct));
        state.Remember(chat, text, string.Join(" ", done));
    }

    private async Task<JsonArray> MessagesAsync(long chat, string text, CancellationToken ct)
    {
        var mode = await arm.GetAsync(ct);
        var zones = await db.Zones.AsNoTracking().OrderBy(z => z.Sort).ThenBy(z => z.Id).Select(z => z.Name).ToListAsync(ct);
        var cameras = await db.Devices.AsNoTracking().Where(d => d.TypeCode == "guard-cam" && d.Status != DeviceStatus.Disabled)
            .OrderBy(d => d.Name).Select(d => d.Name + (d.Zone != null ? $" (зона {d.Zone.Name})" : "")).ToListAsync(ct);
        var outline = await notes.OutlineAsync(200, ct);
        var guard = mode.Mode switch { ArmService.Arriving => "подготовка к приезду", ArmService.ArmedMode => "включена", _ => "снята" };

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = AssistantTools.SystemPrompt(hive.Value.ToLocal(time.GetUtcNow()), guard, zones, cameras, outline) },
        };
        foreach (var (user, assistant) in state.History(chat))
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = user });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistant });
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        return messages;
    }

    /// <summary>Runs a read tool or proposes an action; returns a short line of what was done, for the chat's memory.</summary>
    private async Task<string> RunToolAsync(TgCaller caller, int replyTo, LlmToolCall call, CancellationToken ct)
    {
        var chat = caller.ChatId;
        JsonObject args;
        try
        {
            args = JsonNode.Parse(call.Arguments) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            args = [];
        }

        if (AssistantTools.Actions.Contains(call.Name) && !caller.CanControl)
        {
            await SendAsync(chat, "⛔ Недостаточно прав: у вас роль только для просмотра.", ct, replyTo: replyTo);
            return "[отказано: нет прав]";
        }

        switch (call.Name)
        {
            case AssistantTools.HiveStatus:
                await SendAsync(chat, await bot.StatusAsync(ct), ct, replyTo: replyTo);
                return "[показал общий статус]";
            case AssistantTools.Temperatures:
                await SendAsync(chat, await bot.TemperaturesAsync(ct), ct, replyTo: replyTo);
                return "[показал температуры]";
            case AssistantTools.RecentEvents:
                var count = args["count"] is JsonValue v && v.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, 20) : 5;
                await SendAsync(chat, await bot.EventsAsync(count, ct), ct, replyTo: replyTo);
                return "[показал последние события]";
            case AssistantTools.ZoneStatus:
                return await ZoneStatusAsync(chat, Str(args, "zone"), replyTo, ct);
            case AssistantTools.SetGuard:
                return await ProposeGuardAsync(chat, Str(args, "mode"), replyTo, ct);
            case AssistantTools.TakePhotos:
                return await ProposePhotosAsync(chat, args, replyTo, ct);
            case AssistantTools.AddNote:
                return await ProposeNoteAsync(chat, args, replyTo, ct);
            default:
                return "[неизвестный инструмент]";
        }
    }

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

        var cameras = devices.Where(d => d.TypeCode == "guard-cam").ToList();
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
            .Where(d => d.TypeCode == "guard-cam" && d.Status == DeviceStatus.Online).OrderBy(d => d.Name).ToListAsync(ct);
        var zone = Str(args, "zone");
        var wanted = args["cameras"] is JsonArray list ? list.Select(x => x?.GetValue<string>()).OfType<string>().ToList() : [];
        var scope = "все камеры";
        if (wanted.Count > 0)
        {
            cameras = wanted.Select(w => Pick(cameras, c => c.Name, w)).OfType<Device>().Distinct().ToList();
            scope = "выбранные камеры";
        }
        else if (!string.IsNullOrWhiteSpace(zone))
        {
            var zones = await db.Zones.AsNoTracking().ToListAsync(ct);
            var z = Pick(zones, x => x.Name, zone);
            cameras = z is null ? [] : cameras.Where(c => c.ZoneId == z.Id).ToList();
            scope = z is null ? $"зона «{zone}»" : $"зона «{z.Name}»";
        }
        if (cameras.Count == 0)
        {
            await SendAsync(chat, $"📷 Нет камер на связи ({H(scope)}).", ct, replyTo: replyTo);
            return "[камер нет]";
        }
        var text = $"📷 Сделать снимки сейчас? {H(string.Join(", ", cameras.Select(c => c.Name)))}.";
        await ProposeAsync(chat, AssistantTools.TakePhotos, new JsonObject { ["devices"] = new JsonArray(cameras.Select(c => (JsonNode)c.DeviceId).ToArray()) }, text, replyTo, ct);
        return "[предложил снимки, ждёт подтверждения]";
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

        var match = await notes.MatchPathAsync(path, ct);
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

    private async Task OnButtonAsync(CallbackQuery callback, CancellationToken ct)
    {
        var chat = callback.Message?.Chat.Id ?? callback.From.Id;
        var parts = callback.Data![Prefix.Length..].Split(':', 2);
        var (verb, arg) = parts.Length == 2 ? (parts[0], parts[1]) : (parts[0], "");
        var caller = await bot.CallerForAsync(chat, ct);
        string answer;
        if (caller is null)
            answer = "Чат не привязан";
        else if (!caller.CanControl)
            answer = "⛔ Недостаточно прав";
        else if (verb == "snapzone" && int.TryParse(arg, out var zoneId))
        {
            // The press itself is the confirmation: the button only offers the zone's own cameras.
            var cameras = await db.Devices.AsNoTracking().Where(d => d.ZoneId == zoneId && d.TypeCode == "guard-cam").Select(d => d.DeviceId).ToListAsync(ct);
            await bot.SnapshotAsync(chat, caller.Actor + " via assistant", cameras, callback.Message?.MessageId, ct);
            answer = "Снимаю…";
        }
        else if (state.Take(arg, chat) is not { } action)
        {
            answer = "Запрос устарел, напишите ещё раз";
            await EditAsync(callback, "⌛ Запрос устарел или уже выполнен.", ct);
        }
        else if (verb == "no")
        {
            answer = "Отменено";
            await EditAsync(callback, $"{action.Text}\n\n✖ Отменено.", ct);
        }
        else
        {
            var result = await ExecuteAsync(caller, action, callback.Message?.MessageId, ct);
            answer = "Готово";
            await EditAsync(callback, $"{action.Text}\n\n{result}", ct);
        }
        await telegram.CallAsync(b => b.AnswerCallbackQuery(callback.Id, answer, cancellationToken: ct), ct);
    }

    private async Task<string> ExecuteAsync(TgCaller caller, PendingAction action, int? messageId, CancellationToken ct)
    {
        var actor = caller.Actor + " via assistant";
        try
        {
            switch (action.Tool)
            {
                case AssistantTools.SetGuard:
                    var mode = await arm.SetModeAsync(Str(action.Args, "mode")!, actor, ct);
                    return "✅ " + BotCommands.GuardLine(mode.Mode);
                case AssistantTools.TakePhotos:
                    var devices = action.Args["devices"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
                    await bot.SnapshotAsync(caller.ChatId, actor, devices, messageId, ct);
                    return "✅ Камерам отправлена команда, фото придут ответом.";
                case AssistantTools.AddNote:
                    var path = action.Args["path"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
                    var parent = await notes.EnsurePathAsync(path, actor, ct);
                    var (result, note) = await notes.CreateAsync(new CreateNoteRequest(Str(action.Args, "title")!, parent, Body: Str(action.Args, "text")), actor, ct);
                    return result == NoteChange.Ok
                        ? $"✅ Заметка сохранена{(path.Count > 0 ? $" в «{H(string.Join(" → ", path))}»" : "")}."
                        : "⚠️ Не удалось сохранить заметку.";
                default:
                    return "⚠️ Неизвестное действие.";
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Assistant action {Tool} failed", action.Tool);
            return "⚠️ Не получилось выполнить.";
        }
    }

    /// <summary>The first line of the text, cut to a title's length.</summary>
    internal static string TitleFrom(string body)
    {
        var line = body.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? body;
        return line.Length <= 80 ? line : line[..77].TrimEnd() + "…";
    }

    /// <summary>The item whose name matches the query: exactly (case and ё aside), then by word endings, then by containment.</summary>
    internal static T? Pick<T>(IEnumerable<T> items, Func<T, string> name, string? query) where T : class
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;
        var list = items.ToList();
        var q = NoteService.Normalize(query);
        return list.FirstOrDefault(i => NoteService.Normalize(name(i)) == q)
            ?? list.FirstOrDefault(i => SameWords(name(i), query))
            ?? (q.Length >= 3 ? list.Where(i => NoteService.Normalize(name(i)).Contains(q) || q.Contains(NoteService.Normalize(name(i)))).OrderBy(i => name(i).Length).FirstOrDefault() : null);
    }

    /// <summary>"плантации 1" and "Плантация 1": the same words in the same order, endings aside.</summary>
    private static bool SameWords(string a, string b)
    {
        var x = NoteService.Normalize(a).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var y = NoteService.Normalize(b).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return x.Length == y.Length && x.Zip(y).All(p => p.First == p.Second || NoteService.SameWord(p.First, p.Second));
    }

    internal static string Reading(string metric, double value) => metric switch
    {
        "temperature" => $"🌡 {Temperature(value)}",
        "humidity" => $"💧 {value.ToString("0", Ru)}%",
        "battery" or "battery_pct" => $"🔋 {value.ToString("0", Ru)}%",
        "pressure" => $"⏲ {value.ToString("0", Ru)} гПа",
        "soil_moisture" => $"🌱 {value.ToString("0", Ru)}%",
        _ => $"{metric} {value.ToString("0.##", Ru)}",
    };

    private static string? Str(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private Task<Message> SendAsync(long chat, string text, CancellationToken ct, ReplyMarkup? markup = null, int? replyTo = null) =>
        telegram.CallAsync(b => b.SendMessage(chat, text, ParseMode.Html, replyMarkup: markup,
            replyParameters: replyTo is { } r ? new ReplyParameters { MessageId = r, AllowSendingWithoutReply = true } : null,
            cancellationToken: ct), ct);

    private async Task EditAsync(CallbackQuery callback, string text, CancellationToken ct)
    {
        if (callback.Message is { } m)
            await telegram.CallAsync(b => b.EditMessageText(m.Chat.Id, m.MessageId, text, ParseMode.Html, cancellationToken: ct), ct);
    }
}
