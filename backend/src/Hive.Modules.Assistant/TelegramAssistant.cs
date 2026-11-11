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
public sealed partial class TelegramAssistant(
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
    INoteStore notes,
    AssistantContext context,
    CommandService commands,
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
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = AssistantTools.SystemPrompt(await context.BuildAsync(chat, ct)) },
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

        if (AssistantTools.Actions.Contains(call.Name) && !AssistantTools.Personal.Contains(call.Name) && !caller.CanControl)
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
            case AssistantTools.DeviceProblems:
                await SendAsync(chat, await bot.DevicesAsync(ct), ct, replyTo: replyTo);
                return "[показал проблемы со связью]";
            case AssistantTools.ZoneStatus:
                return await ZoneStatusAsync(chat, Str(args, "zone"), replyTo, ct);
            case AssistantTools.SetGuard:
                return await ProposeGuardAsync(chat, Str(args, "mode"), replyTo, ct);
            case AssistantTools.TakePhotos:
                return await ProposePhotosAsync(chat, args, replyTo, ct);
            case AssistantTools.AddNote:
                return await ProposeNoteAsync(chat, args, replyTo, ct);
            case AssistantTools.Mute:
                return await ProposeMuteAsync(chat, args, replyTo, ct);
            case AssistantTools.MarkViewed:
                return await ProposeViewedAsync(chat, Str(args, "device"), replyTo, ct);
            case AssistantTools.SwitchRelay:
                return await ProposeRelayAsync(chat, args, replyTo, ct);
            case AssistantTools.PressIr:
                return await ProposeIrAsync(chat, Str(args, "device"), Str(args, "button"), replyTo, ct);
            default:
                return "[неизвестный инструмент]";
        }
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
        else if (verb == "snapzone" && !caller.CanControl)
            answer = "⛔ Недостаточно прав";
        else if (verb == "snapzone" && int.TryParse(arg, out var zoneId))
        {
            // The press itself is the confirmation: the button only offers the zone's own cameras.
            var cameras = await db.Devices.AsNoTracking().Where(d => d.ZoneId == zoneId && Cameras.Types.Contains(d.TypeCode)).Select(d => d.DeviceId).ToListAsync(ct);
            await bot.SnapshotAsync(chat, caller.Actor + " via assistant", cameras, callback.Message?.MessageId, ct);
            answer = "Снимаю…";
        }
        else if (state.Take(arg, chat) is not { } action)
        {
            answer = "Запрос устарел, напишите ещё раз";
            await EditAsync(callback, "⌛ Запрос устарел или уже выполнен.", ct);
        }
        else if (verb != "no" && !caller.CanControl && !AssistantTools.Personal.Contains(action.Tool))
        {
            answer = "⛔ Недостаточно прав";
            await EditAsync(callback, $"{action.Text}\n\n⛔ Недостаточно прав.", ct);
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
                    NoteChange result;
                    try
                    {
                        var parent = await notes.EnsurePathAsync(path, actor, ct);
                        (result, _) = await notes.CreateAsync(new CreateNoteRequest(Str(action.Args, "title")!, parent, Body: Str(action.Args, "text")), actor, ct);
                    }
                    catch (NoteMasterUnavailableException)
                    {
                        return "⚠️ Заметки хранятся в городе, а связи с ним сейчас нет: заметка не сохранена.";
                    }
                    return result == NoteChange.Ok
                        ? $"✅ Заметка сохранена{(path.Count > 0 ? $" в «{H(string.Join(" → ", path))}»" : "")}."
                        : "⚠️ Не удалось сохранить заметку.";
                case AssistantTools.Mute:
                    return "✅ " + await bot.MuteChatAsync(caller.ChatId, action.Args["hours"]!.GetValue<double>(), ct);
                case AssistantTools.MarkViewed:
                    var marked = await events.AcknowledgeManyAsync(new AckAllRequest(Str(action.Args, "device")), actor, ct);
                    return $"✅ Отмечено просмотренными: {marked}.";
                case AssistantTools.SwitchRelay:
                    var relay = new JsonObject { ["channel"] = Str(action.Args, "channel"), ["set"] = action.Args["on"]!.GetValue<bool>() ? "on" : "off" };
                    if (action.Args["minutes"] is JsonValue forMin)
                        relay["for_s"] = forMin.GetValue<int>() * 60;
                    return await SendCommandAsync(Str(action.Args, "device")!, "relay", relay, actor, ct);
                case AssistantTools.PressIr:
                    return await SendCommandAsync(Str(action.Args, "device")!, "ir", new JsonObject { ["button"] = Str(action.Args, "button") }, actor, ct);
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

    private async Task<string> SendCommandAsync(string deviceId, string name, JsonObject payload, string actor, CancellationToken ct)
    {
        var (status, _) = await commands.SendAsync(deviceId, name, payload, actor, null, ct);
        return status switch
        {
            SendStatus.Sent => "✅ Команда отправлена.",
            SendStatus.UnknownDevice => "⚠️ Устройство не найдено.",
            SendStatus.BadRequest => "⚠️ Устройство не принимает такую команду.",
            _ => "⚠️ Нет связи с брокером MQTT, команда не ушла.",
        };
    }
}
