using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests.Integration;

/// <summary>
/// An OpenAI-compatible chat completions API on a local port: answers each user text with what the test scripted for it
/// (a tool call or plain text), and keeps every request to check what the hive sent.
/// </summary>
public sealed class FakeLlm : IAsyncDisposable
{
    public const string ApiKey = "sk-test";

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, JsonObject> _script = new();
    private int _callId;

    public ConcurrentQueue<JsonObject> Requests { get; } = new();

    public string BaseUrl { get; private set; } = "";

    public bool Fail { get; set; }

    private FakeLlm(WebApplication app) => _app = app;

    public static async Task<FakeLlm> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeLlm(app);
        app.MapPost("/chat/completions", (Func<HttpContext, Task<IResult>>)fake.HandleAsync);
        await app.StartAsync();
        fake.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return fake;
    }

    /// <summary>When the user writes <paramref name="text"/>, the model calls these tools.</summary>
    public void OnText(string text, params (string Tool, object Args)[] calls) =>
        _script[text] = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = $"call_{Interlocked.Increment(ref _callId)}",
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = c.Tool,
                    ["arguments"] = System.Text.Json.JsonSerializer.Serialize(c.Args),
                },
            }).ToArray()),
        };

    /// <summary>When the user writes <paramref name="text"/>, the model answers in words.</summary>
    public void Answer(string text, string answer) =>
        _script[text] = new JsonObject { ["role"] = "assistant", ["content"] = answer };

    /// <summary>The request whose last user message was <paramref name="text"/>.</summary>
    public JsonObject? RequestFor(string text) =>
        Requests.LastOrDefault(r => LastUserText(r) == text);

    private async Task<IResult> HandleAsync(HttpContext ctx)
    {
        if (ctx.Request.Headers.Authorization.ToString() != $"Bearer {ApiKey}")
            return Results.Json(new { error = new { message = "bad key" } }, statusCode: 401);
        var body = (await JsonNode.ParseAsync(ctx.Request.Body))!.AsObject();
        Requests.Enqueue(body);
        if (Fail)
            return Results.Json(new { error = new { message = "Service unavailable" } }, statusCode: 503);
        var message = _script.TryGetValue(LastUserText(body) ?? "", out var scripted)
            ? scripted.DeepClone()
            : new JsonObject { ["role"] = "assistant", ["content"] = "Не понял." };
        var reply = new JsonObject
        {
            ["id"] = "chatcmpl-test",
            ["object"] = "chat.completion",
            ["model"] = body["model"]?.DeepClone(),
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = "stop" }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 900, ["completion_tokens"] = 30, ["total_tokens"] = 930 },
        };
        return Results.Text(reply.ToJsonString(), "application/json");
    }

    private static string? LastUserText(JsonObject request) =>
        request["messages"]?.AsArray().LastOrDefault(m => m?["role"]?.GetValue<string>() == "user")?["content"]?.GetValue<string>();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
