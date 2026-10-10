using System.Net;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace Hive.Modules.Notify.Telegram;

public sealed class TelegramEndpoint
{
    /// <summary>Bot API base, e.g. a reverse proxy on the VPS (spec 9.1). Empty means https://api.telegram.org.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>socks5://host:port or http://host:port of the VPS proxy. Empty means a direct connection.</summary>
    public string? Proxy { get; set; }

    public string? ProxyUser { get; set; }
    public string? ProxyPassword { get; set; }

    public override string ToString() => $"{BaseUrl ?? "api.telegram.org"}{(Proxy is null ? "" : $" via {new Uri(Proxy).Host}")}";
}

public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    /// <summary>Bot token from @BotFather; only in .env on the node (spec 9.4). Empty disables the bot.</summary>
    public string? Token { get; set; }

    /// <summary>Tried in order; on network errors the next one is used (spec 9.1 "list").</summary>
    public List<TelegramEndpoint> Endpoints { get; set; } = [];

    public int PollTimeoutS { get; set; } = 30;

    /// <summary>Same alarm from the same device within this window edits the first message to "×N" (spec 6.7).</summary>
    public int GroupWindowS { get; set; } = 180;

    /// <summary>critical repeats until acknowledged (spec 6.7).</summary>
    public int CriticalRepeatMin { get; set; } = 5;

    /// <summary>How long to wait for photos still uploading from the hornet (spec 9.5).</summary>
    public int PhotoWaitS { get; set; } = 15;

    public bool Enabled => !string.IsNullOrWhiteSpace(Token);
}

/// <summary>
/// Telegram Bot API client with endpoint failover. Network trouble moves to the next endpoint;
/// API errors (bad request, 403 blocked by user) are returned to the caller as they are.
/// </summary>
public sealed class TelegramGateway : IDisposable
{
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramGateway> _logger;
    private readonly List<(TelegramEndpoint Endpoint, TelegramBotClient Client, HttpClient Http)> _clients = [];
    private int _current;

    public TelegramGateway(IOptions<TelegramOptions> options, ILogger<TelegramGateway> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (!_options.Enabled)
            return;
        var endpoints = _options.Endpoints.Count > 0 ? _options.Endpoints : [new TelegramEndpoint()];
        foreach (var endpoint in endpoints)
        {
            // A proxy may answer gzip-compressed; without decompression the library reads compressed bytes as JSON.
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            };
            if (!string.IsNullOrEmpty(endpoint.Proxy))
            {
                handler.Proxy = new WebProxy(endpoint.Proxy)
                {
                    Credentials = endpoint.ProxyUser is null ? null : new NetworkCredential(endpoint.ProxyUser, endpoint.ProxyPassword),
                };
                handler.UseProxy = true;
            }
            // Long polling holds the request for PollTimeoutS; leave room for slow 4G.
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(_options.PollTimeoutS + 30) };
            // The library appends "/bot<token>/<method>"; a trailing slash in the setting would make "//bot…", which a
            // path-prefix proxy forwards as an unknown URL and Telegram answers with an HTML page.
            var client = new TelegramBotClient(new TelegramBotClientOptions(_options.Token!, string.IsNullOrEmpty(endpoint.BaseUrl) ? null : endpoint.BaseUrl.TrimEnd('/')), http);
            _clients.Add((endpoint, client, http));
        }
    }

    public bool Enabled => _clients.Count > 0;

    public string CurrentEndpoint => Enabled ? _clients[_current].Endpoint.ToString() : "disabled";

    public async Task<T> CallAsync<T>(Func<ITelegramBotClient, Task<T>> call, CancellationToken ct)
    {
        if (!Enabled)
            throw new InvalidOperationException("Telegram is not configured (Telegram:Token)");
        Exception? last = null;
        for (var attempt = 0; attempt < _clients.Count; attempt++)
        {
            var index = _current;
            try
            {
                return await call(_clients[index].Client);
            }
            catch (Exception ex) when (IsNetworkError(ex) && !ct.IsCancellationRequested)
            {
                last = ex;
                _current = (index + 1) % _clients.Count;
                if (_clients.Count > 1)
                    _logger.LogWarning("Telegram endpoint {Endpoint} failed ({Error}); switching to {Next}",
                        _clients[index].Endpoint, ex.Message, _clients[_current].Endpoint);
            }
        }
        throw last!;
    }

    public Task CallAsync(Func<ITelegramBotClient, Task> call, CancellationToken ct) =>
        CallAsync(async c => { await call(c); return true; }, ct);

    /// <summary>
    /// A reply that cannot be read (JSON shape the library does not know) is not a network problem: switching
    /// endpoints would not help, and the poller handles it by skipping that batch.
    /// </summary>
    public static bool IsNetworkError(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException
        || (ex is RequestException && ex is not ApiRequestException && !IsUnreadableReply(ex));

    public static bool IsUnreadableReply(Exception ex) => ex is RequestException && ex.InnerException is JsonException;

    /// <summary>
    /// Ids of the updates waiting for us, read from the raw reply, plus the shape of that reply (field names and
    /// types, never values). Used to move the offset past a batch the library cannot deserialize, and to log what
    /// the batch looked like without logging the messages.
    /// </summary>
    public async Task<(List<long> Ids, string Shape)> PeekUpdatesAsync(int? offset, CancellationToken ct)
    {
        var ids = new List<long>();
        if (!Enabled)
            return (ids, "");
        var endpoint = _clients[_current].Endpoint;
        var baseUrl = string.IsNullOrEmpty(endpoint.BaseUrl) ? "https://api.telegram.org" : endpoint.BaseUrl.TrimEnd('/');
        var body = new Dictionary<string, object?> { ["limit"] = 50, ["timeout"] = 0 };
        if (offset is not null)
            body["offset"] = offset;
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _clients[_current].Http.PostAsync($"{baseUrl}/bot{_options.Token}/getUpdates", content, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
            foreach (var item in result.EnumerateArray())
                if (item.TryGetProperty("update_id", out var id) && id.TryGetInt64(out var value))
                    ids.Add(value);
        return (ids, Shape(doc.RootElement));
    }

    /// <summary>
    /// The structure of a JSON value: field names and types, the first element of each array, no values.
    /// Example: {ok:bool,result:[{update_id:num,message:{text:str}}]}
    /// </summary>
    public static string Shape(JsonElement element)
    {
        var text = ShapeOf(element);
        return text.Length <= 1500 ? text : text[..1500] + "…";
    }

    private static string ShapeOf(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().Select(p => $"{p.Name}:{ShapeOf(p.Value)}")) + "}",
        JsonValueKind.Array => "[" + (e.GetArrayLength() > 0 ? ShapeOf(e.EnumerateArray().First()) : "") + "]",
        JsonValueKind.Number => "num",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Null => "null",
        _ => "str",
    };

    public void Dispose()
    {
        foreach (var (_, _, http) in _clients)
            http.Dispose();
    }
}
