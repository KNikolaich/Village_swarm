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

/// <summary>
/// Two nodes, one stream of events (spec 11): the home node and the city node run the same stack; the city watches home with
/// heartbeats and takes over the alarms when home is gone. Off unless <c>Cluster__Enabled</c> is set.
/// </summary>
public sealed class ClusterOptions
{
    public const string Section = "Cluster";

    public bool Enabled { get; set; }

    public int HeartbeatSeconds { get; set; } = 30;

    /// <summary>No heartbeat from home this long: a warning, and the city may take over if hornets reach it directly.</summary>
    public int HomeSilenceS { get; set; } = 180;

    /// <summary>No heartbeat this long: an alarm.</summary>
    public int HomeAlarmS { get; set; } = 900;

    /// <summary>How often the watchdog looks (seconds).</summary>
    public int TickS { get; set; } = 5;

    /// <summary>Home: how often it looks whether the catalog changed, and the longest it goes without publishing it (seconds).</summary>
    public int CatalogCheckS { get; set; } = 60;

    public int CatalogEveryS { get; set; } = 300;

    /// <summary>City only: the login home's Mosquitto bridge uses on the city broker (created when missing).</summary>
    public string BridgeUser { get; set; } = "bridge-home";

    public string? BridgePassword { get; set; }
}

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

public sealed record ClusterDto(bool Enabled, string Node, string Role, HomeStatusDto? Home, string? ActiveNode, DateTimeOffset? CatalogAt, CatalogCounts? Catalog);

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

/// <summary>Says "I am alive" every 30 seconds (spec 4.2, 11.4) and, at home, takes the active role back when the city had it.</summary>
public sealed class HeartbeatPublisher(
    IOptions<ClusterOptions> options,
    IOptions<HiveOptions> hive,
    NodeState node,
    IServiceProvider services,
    TimeProvider time,
    ILogger<HeartbeatPublisher> logger) : BackgroundService, IMqttMessageHandler
{
    private volatile bool _cityHasTheRole;

    // Resolved when needed: the gateway itself is built from the list of message handlers, this class among them.
    private MqttGateway Mqtt => services.GetRequiredService<MqttGateway>();

    // The active node is only listened to at home: that is where the role is taken back.
    public IReadOnlyList<string> TopicFilters => options.Value.Enabled && hive.Value.Node == "home" ? [Topics.HiveActive] : [];

    public ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt)
    {
        if (topic != Topics.HiveActive)
            return ValueTask.CompletedTask;
        try
        {
            _cityHasTheRole = JsonDocument.Parse(payload).RootElement.TryGetProperty("node", out var n) && n.GetString() == "city";
        }
        catch (JsonException)
        {
            // not ours
        }
        return ValueTask.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider" || !options.Value.Enabled)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.HeartbeatSeconds)), time);
        try
        {
            do
            {
                try
                {
                    await BeatAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Heartbeat failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task BeatAsync(CancellationToken ct)
    {
        if (!Mqtt.IsConnected)
            return;
        var now = time.GetUtcNow();
        var self = hive.Value.Node == "city" ? BrokerNode.City : BrokerNode.Home;
        var beat = new HeartbeatMessage
        {
            Ts = now.ToUnixTimeMilliseconds(),
            Node = self,
            Role = node.Role switch { NodeRole.Active => HiveRole.Active, NodeRole.Standby => HiveRole.Standby, _ => HiveRole.Observer },
            Version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
        };
        await Mqtt.PublishAsync(Topics.HiveHeartbeat(hive.Value.Node), JsonSerializer.Serialize(beat, ContractJson.Options), retain: false, ct);

        // Home is back and the city still says it is in charge: take the role back (spec 11.3).
        if (self == BrokerNode.Home && _cityHasTheRole)
        {
            _cityHasTheRole = false;
            await Mqtt.PublishAsync(Topics.HiveActive,
                JsonSerializer.Serialize(new ActiveNodeMessage { Ts = now.ToUnixTimeMilliseconds(), Node = BrokerNode.Home }, ContractJson.Options), retain: true, ct);
        }
    }
}

/// <summary>Creates the login the home bridge uses, on the city's broker, when missing.</summary>
public sealed class BridgeLogin(MqttGateway mqtt, DynamicSecurity dynsec, IOptions<ClusterOptions> options, IOptions<HiveOptions> hive) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || hive.Value.Node != "city" || string.IsNullOrEmpty(options.Value.BridgePassword))
            return Task.CompletedTask;
        mqtt.Connected += Provision;
        // Not awaited: the broker's first answers can take ten seconds (the api has no permissions yet), and a host must start without waiting.
        if (mqtt.IsConnected)
            _ = Task.Run(async () =>
            {
                try
                {
                    await Provision(CancellationToken.None);
                }
                catch (Exception)
                {
                    // the next (re)connect of the api tries again
                }
            }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private Task Provision(CancellationToken ct) => dynsec.ProvisionBridgeAsync(options.Value.BridgeUser, options.Value.BridgePassword!, ct);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        mqtt.Connected -= Provision;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The observer node only reads (spec 11.3): everything that would change the house is refused. Signing in and a person's own
/// settings (<c>/api/me</c>: password, linking a Telegram chat to this node's bot) stay open: they change nothing of the house.
/// </summary>
public sealed class ReadOnlyMiddleware(RequestDelegate next, NodeState node)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var changes = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);
        if (node.ReadOnly && changes && request.Path.StartsWithSegments("/api") && !request.Path.StartsWithSegments("/api/auth") && !request.Path.StartsWithSegments("/api/me"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { title = "Этот узел только наблюдает: изменения вносятся на главном узле (дома)" });
            return;
        }
        await next(context);
    }
}

public static class ClusterModule
{
    public const string Name = "cluster";

    public static IServiceCollection AddClusterModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ClusterOptions>(configuration.GetSection(ClusterOptions.Section));
        services.AddSingleton<HomeWatch>();
        services.AddSingleton<IMqttMessageHandler>(sp => sp.GetRequiredService<HomeWatch>());
        services.AddHostedService(sp => sp.GetRequiredService<HomeWatch>());
        services.AddSingleton<HeartbeatPublisher>();
        services.AddSingleton<IMqttMessageHandler>(sp => sp.GetRequiredService<HeartbeatPublisher>());
        services.AddHostedService(sp => sp.GetRequiredService<HeartbeatPublisher>());
        services.AddHostedService<BridgeLogin>();

        // The catalog of reference data, home to city (spec 11.2).
        services.AddSingleton<CatalogState>();
        services.AddScoped<CatalogBuilder>();
        services.AddScoped<CatalogApplier>();
        services.AddSingleton<CatalogReceiver>();
        services.AddSingleton<IMqttMessageHandler>(sp => sp.GetRequiredService<CatalogReceiver>());
        services.AddHostedService<CatalogPublisher>();
        return services;
    }

    public static IApplicationBuilder UseClusterReadOnly(this IApplicationBuilder app) => app.UseMiddleware<ReadOnlyMiddleware>();

    public static IEndpointRouteBuilder MapClusterEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cluster", (HomeWatch watch) => TypedResults.Ok(watch.Describe())).WithTags("cluster");
        return app;
    }
}
