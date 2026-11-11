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
    private long _cityRoleSinceMs;

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
            var active = JsonDocument.Parse(payload).RootElement;
            _cityHasTheRole = active.TryGetProperty("node", out var n) && n.GetString() == "city";
            if (_cityHasTheRole && active.TryGetProperty("ts", out var ts) && ts.TryGetInt64(out var since))
                Interlocked.Exchange(ref _cityRoleSinceMs, since);
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
            // The city saw what hornets said to it meanwhile: ask for it (spec 11.3).
            services.GetRequiredService<ReplicaCatchUp>().Want(DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _cityRoleSinceMs)));
            await Mqtt.PublishAsync(Topics.HiveActive,
                JsonSerializer.Serialize(new ActiveNodeMessage { Ts = now.ToUnixTimeMilliseconds(), Node = BrokerNode.Home }, ContractJson.Options), retain: true, ct);
        }
    }
}
