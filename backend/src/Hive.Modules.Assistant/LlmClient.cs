using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Assistant;

public sealed class AssistantOptions
{
    public const string Section = "Assistant";

    /// <summary>The LLM's API key (DeepSeek by default). Empty: no assistant, the bot answers commands only.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Any OpenAI-compatible chat completions API: DeepSeek, or another provider with its own base URL.</summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    public string Model { get; set; } = "deepseek-flash";

    /// <summary>Optional HTTP or SOCKS5 proxy for the LLM (DeepSeek is reachable without one).</summary>
    public string? Proxy { get; set; }

    public int TimeoutS { get; set; } = 40;

    /// <summary>A guard against a runaway loop or a leaked chat: requests in words per day, for all chats together.</summary>
    public int MaxRequestsPerDay { get; set; } = 300;

    /// <summary>How long a proposed action waits for its button.</summary>
    public int ConfirmMinutes { get; set; } = 5;

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed record LlmToolCall(string Id, string Name, string Arguments);

public sealed record LlmReply(string? Content, IReadOnlyList<LlmToolCall> ToolCalls, int PromptTokens, int CompletionTokens);

/// <summary>
/// A thin OpenAI-compatible chat completions client (DeepSeek speaks this protocol). Only what the assistant needs:
/// messages in, text or tool calls out, token counts for the journal.
/// </summary>
public sealed class LlmClient(HttpClient http, IOptions<AssistantOptions> options)
{
    public async Task<LlmReply> CompleteAsync(JsonArray messages, JsonArray tools, CancellationToken ct)
    {
        var o = options.Value;
        var body = new JsonObject
        {
            ["model"] = o.Model,
            ["messages"] = messages.DeepClone(),
            ["tools"] = tools.DeepClone(),
            ["tool_choice"] = "auto",
            ["temperature"] = 0,
            ["max_tokens"] = 1024,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"LLM answered {(int)response.StatusCode}: {text[..Math.Min(300, text.Length)]}", null, response.StatusCode);
        return Parse(text);
    }

    internal static LlmReply Parse(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new JsonException("Empty LLM reply");
        var message = root["choices"]?[0]?["message"] ?? throw new JsonException("LLM reply without a message");
        var calls = new List<LlmToolCall>();
        foreach (var call in message["tool_calls"]?.AsArray() ?? [])
        {
            var function = call?["function"];
            if (function?["name"]?.GetValue<string>() is not { } name)
                continue;
            calls.Add(new LlmToolCall(call!["id"]?.GetValue<string>() ?? "", name, function["arguments"]?.GetValue<string>() ?? "{}"));
        }
        var usage = root["usage"];
        return new LlmReply(message["content"]?.GetValue<string>(), calls,
            usage?["prompt_tokens"]?.GetValue<int>() ?? 0, usage?["completion_tokens"]?.GetValue<int>() ?? 0);
    }
}
