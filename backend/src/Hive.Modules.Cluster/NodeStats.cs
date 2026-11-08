using System.Reflection;
using System.Text.Json;
using Hive.Contracts;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Telemetry;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Cluster;

/// <summary>What one node says about its own board: the readings of its hive device.</summary>
public sealed record NodeStatsMessage(int V, long Ts, string Node, IReadOnlyDictionary<string, double> Values, string? Alias = null, IReadOnlyList<string>? Caps = null);

/// <summary>
/// Both nodes: tells the other one how the board is doing (retained over the bridge), so that both hives show in the swarm
/// with their own CPU temperature, load, memory and disk.
/// </summary>
public sealed class NodeStatsPublisher(
    IOptions<ClusterOptions> options,
    IOptions<HiveOptions> hive,
    HiveStats stats,
    IServiceProvider services,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<NodeStatsPublisher> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private MqttGateway Mqtt => services.GetRequiredService<MqttGateway>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider" || !options.Value.Enabled)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.StatsEveryS)), time);
        try
        {
            do
            {
                try
                {
                    await PublishAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Publishing the hive readings failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task PublishAsync(CancellationToken ct)
    {
        var (at, values) = stats.Latest();
        if (at is null || values.Count == 0 || !Mqtt.IsConnected)
            return;
        using var scope = scopes.CreateScope();
        var alias = await scope.ServiceProvider.GetRequiredService<HiveAliasService>().GetAsync(ct);
        // The city says it keeps the notes (once updated and unless switched off): the dacha moves its notes only after hearing this.
        IReadOnlyList<string> caps = hive.Value.Node == "city" && options.Value.NotesInCity ? [NotesMasterPolicy.Cap] : [];
        var message = new NodeStatsMessage(1, at.Value.ToUnixTimeMilliseconds(), hive.Value.Node, values, alias, caps);
        await Mqtt.PublishAsync(Topics.HiveStats(hive.Value.Node), JsonSerializer.Serialize(message, Json), retain: true, ct);
    }
}

/// <summary>
/// Listens to the other node's readings and keeps them as telemetry of device hive-{other}. The city's hive device is made here on the
/// first message; home's device on the city is made by the watch on home, so only its readings are added. Silence for three intervals
/// is "offline" (except for home, whose state the watch keeps).
/// </summary>
public sealed class NodeStatsReceiver(
    IOptions<ClusterOptions> options,
    IOptions<HiveOptions> hive,
    IngestQueue ingest,
    TimeProvider time) : BackgroundService, IMqttMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string Other => hive.Value.Node == "city" ? "home" : "city";

    private string OtherDevice => $"hive-{Other}";

    private TimeSpan Stale => TimeSpan.FromSeconds(3 * Math.Max(5, options.Value.StatsEveryS));

    private readonly Lock _gate = new();
    private bool _known;
    private DateTimeOffset _lastAt = DateTimeOffset.MinValue;
    private bool _online;

    /// <summary>How the other hive calls itself (from its last message), for the menu and the swarm.</summary>
    public string? OtherAlias { get; private set; }

    /// <summary>The other hive said it is the master of the notes (what it said last, even when it has gone quiet since).</summary>
    public bool NotesMasterDeclared { get; private set; }

    public IReadOnlyList<string> TopicFilters => options.Value.Enabled ? [Topics.HiveStats(Other)] : [];

    public ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt)
    {
        if (topic != Topics.HiveStats(Other) || payload.IsEmpty)
            return ValueTask.CompletedTask;
        NodeStatsMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<NodeStatsMessage>(payload.Span, Json);
        }
        catch (JsonException)
        {
            return ValueTask.CompletedTask;
        }
        if (message is not { V: 1 } || message.Node != Other || message.Values.Count == 0)
            return ValueTask.CompletedTask;
        Accept(message, receivedAt);
        return ValueTask.CompletedTask;
    }

    internal void Accept(NodeStatsMessage message, DateTimeOffset receivedAt)
    {
        var sentAt = DateTimeOffset.FromUnixTimeMilliseconds(message.Ts);
        OtherAlias = string.IsNullOrWhiteSpace(message.Alias) ? null : message.Alias;
        NotesMasterDeclared = message.Caps?.Contains(NotesMasterPolicy.Cap) == true;
        // A retained message older than a few intervals says what the board did while the link was down, not how it is now.
        var fresh = receivedAt - sentAt < Stale;
        bool announce, wake;
        lock (_gate)
        {
            announce = !_known;
            _known = true;
            if (fresh)
                _lastAt = receivedAt;
            wake = fresh && !_online;
            _online |= fresh;
        }
        if (announce && Other == "city")
        {
            using var empty = JsonDocument.Parse("{}");
            ingest.Enqueue(new DeviceInfoReceived(OtherDevice, Ulid.New(receivedAt), receivedAt, receivedAt, "hive", "", "", "", null, 0, empty));
        }
        ingest.Enqueue(new TelemetryReceived(OtherDevice, Ulid.New(receivedAt), sentAt, receivedAt,
            message.Values.Select(v => (v.Key, v.Value)).ToList()));
        if (wake && Other == "city")
            ingest.Enqueue(new DeviceStatusChanged(OtherDevice, true, receivedAt));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider" || !options.Value.Enabled)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.StatsEveryS)), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                CheckSilence(time.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal void CheckSilence(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_online || now - _lastAt < Stale)
                return;
            _online = false;
        }
        if (Other == "city")
            ingest.Enqueue(new DeviceStatusChanged(OtherDevice, false, now));
    }
}
