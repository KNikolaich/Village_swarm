using System.Reflection;
using System.Text;
using System.Text.Json;
using Hive.Contracts;
using Hive.Contracts.Messages;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Cluster;

public enum HomePhase { Up, Silent, SilentLong }

public enum HomeChange { Silent, SilentLong, Back }

/// <summary>
/// The watchdog's memory of home, with no clock of its own (spec 11.4): feed it heartbeats and the time, it says what changed.
/// Silence is counted from the last heartbeat, or from the start of the watch before the first one.
/// </summary>
public sealed class HomeWatchLogic(DateTimeOffset startedAt, TimeSpan silence, TimeSpan alarm)
{
    public HomePhase Phase { get; private set; } = HomePhase.Up;

    public DateTimeOffset? LastHeartbeat { get; private set; }

    /// <summary>When the current silence began (the last heartbeat, or the start).</summary>
    public DateTimeOffset SilentSince => LastHeartbeat ?? startedAt;

    public TimeSpan SilentFor(DateTimeOffset now) => now - SilentSince;

    /// <summary>Back when home was reported silent and now speaks again.</summary>
    public HomeChange? OnHeartbeat(DateTimeOffset now)
    {
        var wasDown = Phase != HomePhase.Up;
        LastHeartbeat = now;
        Phase = HomePhase.Up;
        return wasDown ? HomeChange.Back : null;
    }

    public IReadOnlyList<HomeChange> OnTick(DateTimeOffset now)
    {
        var changes = new List<HomeChange>();
        var quiet = SilentFor(now);
        if (quiet >= silence && Phase == HomePhase.Up)
        {
            Phase = HomePhase.Silent;
            changes.Add(HomeChange.Silent);
        }
        if (quiet >= alarm && Phase == HomePhase.Silent)
        {
            Phase = HomePhase.SilentLong;
            changes.Add(HomeChange.SilentLong);
        }
        return changes;
    }
}

public sealed record HomeStatusDto(string Phase, DateTimeOffset? LastHeartbeatAt, int SilentSeconds);

public sealed record ClusterDto(
    bool Enabled, string Node, string Role, HomeStatusDto? Home, string? ActiveNode, DateTimeOffset? CatalogAt, CatalogCounts? Catalog,
    bool TelegramConfigured = true, int TelegramChats = 0, string? Alias = null, string? OtherAlias = null, string NotesIn = "local");

/// <summary>
/// The city's watch on home (spec 11.4): heartbeats in, events out ("home_offline" warn, "home_offline_long" alarm, "home_online"),
/// and the switch to standby-active when home is silent and hornets are talking to the city broker directly (spec 11.3).
/// </summary>
public sealed class HomeWatch(
    IOptions<ClusterOptions> options,
    IOptions<HiveOptions> hive,
    NodeState node,
    IngestQueue ingest,
    IServiceProvider services,
    IServiceScopeFactory scopes,
    CatalogState catalog,
    TimeProvider time,
    ILogger<HomeWatch> logger) : BackgroundService, IMqttMessageHandler
{
    public const string HomeDevice = "hive-home";

    // Resolved when needed: the gateway itself is built from the list of message handlers, this class among them.
    private MqttGateway Mqtt => services.GetRequiredService<MqttGateway>();

    private readonly Lock _gate = new();
    private HomeWatchLogic? _logic;
    private bool _deviceReady;
    private bool _directDevice;
    private string? _activeNode;

    private bool Watching => options.Value.Enabled && hive.Value.Node == "city";

    public IReadOnlyList<string> TopicFilters => Watching
        ? [Topics.HiveHeartbeat("home"), $"{Topics.Prefix}/dev/+/status", $"{Topics.Prefix}/dev/+/health", Topics.HiveActive]
        : [];

    public string? ActiveNode => _activeNode;

    public ClusterDto Describe()
    {
        var now = time.GetUtcNow();
        HomeStatusDto? home = null;
        lock (_gate)
        {
            if (_logic is not null)
                home = new HomeStatusDto(_logic.Phase.ToString(), _logic.LastHeartbeat, (int)Math.Max(0, _logic.SilentFor(now).TotalSeconds));
        }
        return new ClusterDto(options.Value.Enabled, hive.Value.Node, node.Role.ToString(), home, _activeNode, catalog.AppliedAt, catalog.Counts);
    }

    public ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt)
    {
        if (!Watching)
            return ValueTask.CompletedTask;
        if (topic == Topics.HiveHeartbeat("home"))
            return new ValueTask(OnHeartbeatAsync(receivedAt));
        if (topic == Topics.HiveActive)
        {
            try
            {
                _activeNode = JsonDocument.Parse(payload).RootElement.TryGetProperty("node", out var n) ? n.GetString() : null;
            }
            catch (JsonException)
            {
                // a retained message that is not ours: ignored
            }
            return ValueTask.CompletedTask;
        }
        // A hornet speaking on the city broker while home is silent reached it directly: the bridge is not what carried it.
        if (!topic.StartsWith($"{Topics.Prefix}/dev/", StringComparison.Ordinal) || !(topic.EndsWith("/status", StringComparison.Ordinal) || topic.EndsWith("/health", StringComparison.Ordinal)))
            return ValueTask.CompletedTask;
        lock (_gate)
        {
            if (_logic is { Phase: not HomePhase.Up })
                _directDevice = true;
        }
        return ValueTask.CompletedTask;
    }

    private async Task OnHeartbeatAsync(DateTimeOffset at)
    {
        HomeChange? change;
        TimeSpan down = TimeSpan.Zero;
        lock (_gate)
        {
            _logic ??= NewLogic(at);
            down = _logic.SilentFor(at);
            change = _logic.OnHeartbeat(at);
            _directDevice = false;
        }
        if (change is null)
            return;
        // Home is back: the observer role again (home takes the active role back by publishing it itself).
        node.Set(NodeRole.Observer);
        await EmitAsync("home_online", EventSeverity.Info, new { downSeconds = (int)down.TotalSeconds }, at);
    }

    private HomeWatchLogic NewLogic(DateTimeOffset at) =>
        new(at, TimeSpan.FromSeconds(options.Value.HomeSilenceS), TimeSpan.FromSeconds(options.Value.HomeAlarmS));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The build runs the app to write the OpenAPI document; it must not need a database.
        if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider" || !Watching)
            return;
        lock (_gate)
            _logic ??= NewLogic(time.GetUtcNow());
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.TickS)), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Home watch round failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        IReadOnlyList<HomeChange> changes;
        bool standBy;
        int silentSeconds;
        lock (_gate)
        {
            _logic ??= NewLogic(now);
            changes = _logic.OnTick(now);
            silentSeconds = (int)_logic.SilentFor(now).TotalSeconds;
            standBy = _logic.Phase != HomePhase.Up && _directDevice;
        }
        foreach (var change in changes)
        {
            if (change == HomeChange.Silent)
                await EmitAsync("home_offline", EventSeverity.Warn, new { seconds = silentSeconds }, now);
            else if (change == HomeChange.SilentLong)
                await EmitAsync("home_offline_long", EventSeverity.Alarm, new { minutes = silentSeconds / 60 }, now);
        }
        if (standBy && node.Role == NodeRole.Observer)
        {
            node.Set(NodeRole.Standby);
            logger.LogWarning("Home is silent and hornets reach the city broker directly: this node takes over the alarms");
            if (Mqtt.IsConnected)
                await Mqtt.PublishAsync(Topics.HiveActive,
                    JsonSerializer.Serialize(new ActiveNodeMessage { Ts = now.ToUnixTimeMilliseconds(), Node = BrokerNode.City }, ContractJson.Options), retain: true, ct);
        }
    }

    /// <summary>The event goes under the device "hive-home", made here when the city does not have it yet.</summary>
    private async Task EmitAsync(string type, EventSeverity severity, object payload, DateTimeOffset at)
    {
        await EnsureHomeDeviceAsync();
        ingest.Enqueue(new DeviceEventReceived(HomeDevice, Ulid.New(at), at, at, type, severity, null, JsonSerializer.SerializeToDocument(payload)));
    }

    private async Task EnsureHomeDeviceAsync()
    {
        if (_deviceReady)
            return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        if (!await db.DeviceTypes.AnyAsync(t => t.Code == "hive"))
            db.DeviceTypes.Add(new DeviceType { Code = "hive", Title = "Улей", OfflineAfterS = 180 });
        if (!await db.Devices.AnyAsync(d => d.DeviceId == HomeDevice))
            db.Devices.Add(new Device { DeviceId = HomeDevice, TypeCode = "hive", Name = "Улей дома", Status = DeviceStatus.Online, CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        _deviceReady = true;
    }
}
