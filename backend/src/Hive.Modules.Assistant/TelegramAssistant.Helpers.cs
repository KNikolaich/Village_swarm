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

/// <summary>Small helpers: matching names, texts of readings.</summary>
public sealed partial class TelegramAssistant
{
    /// <summary>A device by its name (as for zones) or by its id.</summary>
    internal static Device? PickDevice(IEnumerable<Device> devices, string? query)
    {
        var list = devices.ToList();
        return string.IsNullOrWhiteSpace(query) ? null
            : list.FirstOrDefault(d => string.Equals(d.DeviceId, query.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Pick(list, d => d.Name, query);
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
