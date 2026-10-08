using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests.Integration;

/// <summary>
/// Minimal Telegram Bot API on a local port: enough for getMe, getUpdates (long polling), sendMessage,
/// sendMediaGroup, editMessageText and answerCallbackQuery. The api talks to it through Telegram:Endpoints:0:BaseUrl.
/// </summary>
public sealed class FakeTelegram : IAsyncDisposable
{
    public const string Token = "123456:TEST-TOKEN";
    public const string BotUsername = "vs_test_bot";

    private readonly WebApplication _app;
    private readonly Channel<JsonObject> _updates = Channel.CreateUnbounded<JsonObject>();
    private int _messageId = 100;
    private int _updateId = 1;

    public ConcurrentQueue<Sent> Outbox { get; } = new();

    public sealed record Sent(string Method, long ChatId, string? Text, int MessageId, int Photos, int? ReplyTo, bool Silent, string? Buttons);

    public string BaseUrl { get; private set; } = "";

    private FakeTelegram(WebApplication app) => _app = app;

    public static async Task<FakeTelegram> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeTelegram(app);
        app.Map("/bot{token}/{method}", fake.HandleAsync);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake.BaseUrl = address;
        return fake;
    }

    /// <summary>A user typing a message in a private chat.</summary>
    public void UserSays(long chatId, string text, string username = "kirill") =>
        _updates.Writer.TryWrite(new JsonObject
        {
            ["update_id"] = Interlocked.Increment(ref _updateId),
            ["message"] = new JsonObject
            {
                ["message_id"] = Interlocked.Increment(ref _messageId),
                ["date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["chat"] = new JsonObject { ["id"] = chatId, ["type"] = "private", ["username"] = username },
                ["from"] = new JsonObject { ["id"] = chatId, ["is_bot"] = false, ["first_name"] = username, ["username"] = username },
                ["text"] = text,
            },
        });

    /// <summary>A user pressing an inline button under a message the bot sent.</summary>
    public void UserPresses(long chatId, int messageId, string data, string username = "kirill") =>
        _updates.Writer.TryWrite(new JsonObject
        {
            ["update_id"] = Interlocked.Increment(ref _updateId),
            ["callback_query"] = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["chat_instance"] = "x",
                ["data"] = data,
                ["from"] = new JsonObject { ["id"] = chatId, ["is_bot"] = false, ["first_name"] = username, ["username"] = username },
                ["message"] = new JsonObject
                {
                    ["message_id"] = messageId,
                    ["date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["chat"] = new JsonObject { ["id"] = chatId, ["type"] = "private" },
                    ["text"] = "…",
                },
            },
        });

    public async Task<Sent> WaitForAsync(Func<Sent, bool> match, string what, int timeoutS = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutS);
        while (DateTime.UtcNow < deadline)
        {
            if (Outbox.FirstOrDefault(match) is { } found)
                return found;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Telegram never got: {what}. Sent: " +
            string.Join(" | ", Outbox.Select(s => $"{s.Method}({s.ChatId}) {s.Text}")));
    }

    private async Task HandleAsync(HttpContext ctx, string token, string method)
    {
        if (token != Token)
        {
            await Reply(ctx, false, null, 401, "Unauthorized");
            return;
        }
        var form = await ReadParametersAsync(ctx.Request);
        switch (method.ToLowerInvariant())
        {
            case "getme":
                await Reply(ctx, true, new JsonObject { ["id"] = 123456, ["is_bot"] = true, ["first_name"] = "Hive", ["username"] = BotUsername });
                break;
            case "getupdates":
                var timeout = form.TryGetValue("timeout", out var t) ? int.Parse(t!, System.Globalization.CultureInfo.InvariantCulture) : 0;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(Math.Min(timeout, 2)));
                    var batch = new JsonArray();
                    try
                    {
                        if (await _updates.Reader.WaitToReadAsync(cts.Token))
                            while (_updates.Reader.TryRead(out var u))
                                batch.Add(u);
                    }
                    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
                    {
                    }
                    await Reply(ctx, true, batch);
                }
                break;
            case "sendmessage":
            case "editmessagetext":
                var chat = long.Parse(form["chat_id"]!, System.Globalization.CultureInfo.InvariantCulture);
                var id = method.Equals("editmessagetext", StringComparison.OrdinalIgnoreCase)
                    ? int.Parse(form["message_id"]!, System.Globalization.CultureInfo.InvariantCulture)
                    : Interlocked.Increment(ref _messageId);
                Outbox.Enqueue(new Sent(method.ToLowerInvariant(), chat, form.GetValueOrDefault("text"), id, 0,
                    ReplyTo(form), form.GetValueOrDefault("disable_notification") == "true", form.GetValueOrDefault("reply_markup")));
                await Reply(ctx, true, Message(chat, id, form.GetValueOrDefault("text")));
                break;
            case "sendmediagroup":
                var mediaChat = long.Parse(form["chat_id"]!, System.Globalization.CultureInfo.InvariantCulture);
                var media = JsonNode.Parse(form["media"]!)!.AsArray();
                var messages = new JsonArray();
                var first = 0;
                foreach (var _ in media)
                {
                    var mid = Interlocked.Increment(ref _messageId);
                    first = first == 0 ? mid : first;
                    messages.Add(Message(mediaChat, mid, null));
                }
                Outbox.Enqueue(new Sent("sendmediagroup", mediaChat, null, first, media.Count, ReplyTo(form),
                    form.GetValueOrDefault("disable_notification") == "true", null));
                await Reply(ctx, true, messages);
                break;
            case "answercallbackquery":
                Outbox.Enqueue(new Sent("answercallbackquery", 0, form.GetValueOrDefault("text"), 0, 0, null, false, null));
                await Reply(ctx, true, true);
                break;
            default:
                await Reply(ctx, false, null, 400, $"Bad Request: unknown method {method}");
                break;
        }
    }

    private static int? ReplyTo(Dictionary<string, string?> form) =>
        form.TryGetValue("reply_parameters", out var rp) && rp is not null
            ? JsonNode.Parse(rp)!["message_id"]!.GetValue<int>()
            : null;

    private static JsonObject Message(long chat, int id, string? text) => new()
    {
        ["message_id"] = id,
        ["date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["chat"] = new JsonObject { ["id"] = chat, ["type"] = "private" },
        ["text"] = text,
    };

    /// <summary>Telegram.Bot posts JSON, or multipart/form-data when files are attached.</summary>
    private static async Task<Dictionary<string, string?>> ReadParametersAsync(HttpRequest request)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            foreach (var (key, value) in form)
                result[key] = value.ToString();
            return result;
        }
        if (request.ContentLength is null or 0 && !request.Headers.ContainsKey("Transfer-Encoding"))
            return result;
        var json = await JsonNode.ParseAsync(request.Body);
        if (json is JsonObject obj)
            foreach (var (key, value) in obj)
                result[key] = value switch
                {
                    null => null,
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    _ => value.ToJsonString(),
                };
        return result;
    }

    private static Task Reply(HttpContext ctx, bool ok, JsonNode? result, int status = 200, string? description = null)
    {
        ctx.Response.StatusCode = status;
        var body = new JsonObject { ["ok"] = ok };
        if (ok)
            body["result"] = result;
        else
        {
            body["error_code"] = status;
            body["description"] = description;
        }
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(body.ToJsonString());
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
