using System.Net;
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
            var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
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
            var client = new TelegramBotClient(new TelegramBotClientOptions(_options.Token!, string.IsNullOrEmpty(endpoint.BaseUrl) ? null : endpoint.BaseUrl), http);
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

    public static bool IsNetworkError(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException
        || (ex is RequestException && ex is not ApiRequestException);

    public void Dispose()
    {
        foreach (var (_, _, http) in _clients)
            http.Dispose();
    }
}
