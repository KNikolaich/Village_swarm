using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Hive.Contracts;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Cluster;

public sealed record ReplicaRequest(int V, string Id, long Since);

public sealed record ReplicaEvent(string Id, string DeviceId, string Type, string Severity, long Ts, long ReceivedAt, JsonElement Payload, bool? Armed);

public sealed record ReplicaChunk(int V, string RequestId, int Part, int Parts, bool More, long LastTs, IReadOnlyList<ReplicaEvent> Events);

public static class ReplicaCodec
{
    public const string RequestTopic = $"{Topics.Prefix}/hive/home/replica/request";

    public const string ChunkTopic = $"{Topics.Prefix}/sync/city/replica";

    /// <summary>Most events one request brings back, and how many go in one message.</summary>
    public const int MaxEvents = 2000;

    public const int ChunkSize = 100;

    /// <summary>How far before the time the city took the role home asks, to be safe against clocks that differ a little.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] PackChunk(ReplicaChunk chunk)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gzip, chunk, Json);
        return buffer.ToArray();
    }

    public static ReplicaChunk? UnpackChunk(ReadOnlyMemory<byte> payload)
    {
        using var gzip = new GZipStream(new MemoryStream(payload.ToArray()), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<ReplicaChunk>(gzip, Json);
    }

    public static string PackRequest(ReplicaRequest request) => JsonSerializer.Serialize(request, Json);

    public static ReplicaRequest? UnpackRequest(ReadOnlyMemory<byte> payload) => JsonSerializer.Deserialize<ReplicaRequest>(payload.Span, Json);
}

/// <summary>City: answers a request for the events home missed while it was down (spec 11.3, "догружает").</summary>
public sealed class ReplicaServer(
    IOptions<ClusterOptions> options,
    IOptions<HiveOptions> hive,
    IServiceProvider services,
    IServiceScopeFactory scopes,
    ILogger<ReplicaServer> logger) : IMqttMessageHandler
{
    // Resolved when needed: the gateway itself is built from the list of message handlers, this class among them.
    private MqttGateway Mqtt => services.GetRequiredService<MqttGateway>();

    private bool Serving => options.Value.Enabled && hive.Value.Node == "city";

    public IReadOnlyList<string> TopicFilters => Serving ? [ReplicaCodec.RequestTopic] : [];

    public ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt)
    {
        if (topic != ReplicaCodec.RequestTopic || !Serving || retained || payload.IsEmpty)
            return ValueTask.CompletedTask;
        ReplicaRequest? request;
        try
        {
            request = ReplicaCodec.UnpackRequest(payload);
        }
        catch (JsonException)
        {
            return ValueTask.CompletedTask;
        }
        if (request is not { V: 1 } || string.IsNullOrEmpty(request.Id))
            return ValueTask.CompletedTask;
        // Answered on its own task: a publish with acknowledgement waits for the very loop that delivers this message.
        _ = Task.Run(async () =>
        {
            try
            {
                await AnswerAsync(request, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Answering a request for missed events failed");
            }
        });
        return ValueTask.CompletedTask;
    }

    internal async Task AnswerAsync(ReplicaRequest request, CancellationToken ct)
    {
        var since = DateTimeOffset.FromUnixTimeMilliseconds(request.Since);
        List<DeviceEvent> rows;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
            rows = await db.Events.AsNoTracking()
                .Where(e => e.Ts > since && e.DeviceId != HomeWatch.HomeDevice)
                .OrderBy(e => e.Ts).ThenBy(e => e.Id)
                .Take(ReplicaCodec.MaxEvents + 1)
                .ToListAsync(ct);
        }
        var more = rows.Count > ReplicaCodec.MaxEvents;
        if (more)
            rows.RemoveAt(rows.Count - 1);
        var lastTs = rows.Count > 0 ? rows[^1].Ts.ToUnixTimeMilliseconds() : request.Since;
        var events = rows.Select(e => new ReplicaEvent(e.Id, e.DeviceId, e.Type, e.Severity.ToString(), e.Ts.ToUnixTimeMilliseconds(),
            e.ReceivedAt.ToUnixTimeMilliseconds(), e.Payload.RootElement.Clone(), e.Armed)).ToList();
        var parts = Math.Max(1, (events.Count + ReplicaCodec.ChunkSize - 1) / ReplicaCodec.ChunkSize);
        for (var part = 0; part < parts; part++)
        {
            var chunk = new ReplicaChunk(1, request.Id, part, parts, more, lastTs, events.Skip(part * ReplicaCodec.ChunkSize).Take(ReplicaCodec.ChunkSize).ToList());
            await Mqtt.PublishAsync(ReplicaCodec.ChunkTopic, ReplicaCodec.PackChunk(chunk), retain: false, ct);
        }
        logger.LogInformation("Home asked for events since {Since:u}: sent {Count}{More}", since, events.Count, more ? " (more to come)" : "");
    }
}

/// <summary>
/// Home: after it took the active role back from the city it asks for what the city saw meanwhile, and keeps what it did not have.
/// Asked again every minute until the whole answer is in. Nothing is notified and no rule runs for these events: the city already told people.
/// </summary>
public sealed class ReplicaCatchUp(
    IOptions<ClusterOptions> options,
    IOptions<HiveOptions> hive,
    IServiceProvider services,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<ReplicaCatchUp> logger) : BackgroundService, IMqttMessageHandler
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(60);

    // Resolved when needed: the gateway itself is built from the list of message handlers, this class among them.
    private MqttGateway Mqtt => services.GetRequiredService<MqttGateway>();

    private readonly Lock _gate = new();
    private long? _since;
    private string? _requestId;
    private DateTimeOffset _askedAt;
    private readonly HashSet<int> _partsSeen = [];

    private bool Asking => options.Value.Enabled && hive.Value.Node == "home";

    public IReadOnlyList<string> TopicFilters => Asking ? [ReplicaCodec.ChunkTopic] : [];

    public bool Pending
    {
        get
        {
            lock (_gate)
                return _since is not null;
        }
    }

    /// <summary>Home wants the events the city has after this time. Several wishes in a row keep the earliest.</summary>
    public void Want(DateTimeOffset since)
    {
        if (!Asking)
            return;
        var ms = (since - ReplicaCodec.Margin).ToUnixTimeMilliseconds();
        lock (_gate)
        {
            _since = _since is { } old ? Math.Min(old, ms) : ms;
            _askedAt = DateTimeOffset.MinValue;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider" || !Asking)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.TickS)), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await AskIfDueAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Asking the city for missed events failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task AskIfDueAsync(CancellationToken ct)
    {
        string id;
        long since;
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_since is null || now - _askedAt < Retry || !Mqtt.IsConnected)
                return;
            _askedAt = now;
            _requestId = id = Ulid.New(now);
            _partsSeen.Clear();
            since = _since.Value;
        }
        await Mqtt.PublishAsync(ReplicaCodec.RequestTopic, ReplicaCodec.PackRequest(new ReplicaRequest(1, id, since)), retain: false, ct);
        logger.LogInformation("Asked the city for the events since {Since:u}", DateTimeOffset.FromUnixTimeMilliseconds(since));
    }

    public async ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt)
    {
        if (topic != ReplicaCodec.ChunkTopic || !Asking || retained || payload.IsEmpty)
            return;
        ReplicaChunk? chunk;
        try
        {
            chunk = ReplicaCodec.UnpackChunk(payload);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return;
        }
        lock (_gate)
        {
            if (chunk is not { V: 1 } || chunk.RequestId != _requestId)
                return;
        }
        try
        {
            using var scope = scopes.CreateScope();
            var added = await StoreAsync(scope.ServiceProvider.GetRequiredService<HiveDbContext>(), chunk.Events, CancellationToken.None);
            logger.LogInformation("Events from the city (part {Part} of {Parts}): {Added} new", chunk.Part + 1, chunk.Parts, added);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or JsonException)
        {
            logger.LogWarning(ex, "Events from the city could not be stored");
            return;
        }
        lock (_gate)
        {
            _partsSeen.Add(chunk.Part);
            if (_partsSeen.Count < chunk.Parts)
                return;
            // The whole answer is in. When the limit cut it, ask again from where it stopped; otherwise home is up to date.
            if (chunk.More)
            {
                _since = chunk.LastTs;
                _askedAt = DateTimeOffset.MinValue;
            }
            else
            {
                _since = null;
                _requestId = null;
            }
        }
    }

    /// <summary>Keeps the events home does not have (by id) for devices it knows. Returns how many were added.</summary>
    public static async Task<int> StoreAsync(HiveDbContext db, IReadOnlyList<ReplicaEvent> events, CancellationToken ct)
    {
        if (events.Count == 0)
            return 0;
        var ids = events.Select(e => e.Id).ToList();
        var have = (await db.Events.AsNoTracking().Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        var deviceIds = events.Select(e => e.DeviceId).Distinct().ToList();
        var known = (await db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.DeviceId)).Select(d => d.DeviceId).ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var e in events)
        {
            if (have.Contains(e.Id) || !known.Contains(e.DeviceId) || !Enum.TryParse<EventSeverity>(e.Severity, out var severity))
                continue;
            db.Events.Add(new DeviceEvent
            {
                Id = e.Id,
                DeviceId = e.DeviceId,
                Type = e.Type,
                Severity = severity,
                Ts = DateTimeOffset.FromUnixTimeMilliseconds(e.Ts),
                ReceivedAt = DateTimeOffset.FromUnixTimeMilliseconds(e.ReceivedAt),
                Payload = JsonDocument.Parse(e.Payload.GetRawText()),
                Armed = e.Armed,
                ViaNode = HiveNode.City,
            });
            added++;
        }
        await db.SaveChangesAsync(ct);
        return added;
    }
}
