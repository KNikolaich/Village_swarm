using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Mqtt;

/// <summary>
/// Zigbee through zigbee2mqtt (docs/zigbee.md, contracts/zigbee.md): the container talks to the Zigbee dongle and publishes
/// to the same broker under its base topic. The hive reads those topics and gives z2m its own broker login.
/// </summary>
public sealed class ZigbeeOptions
{
    public const string Section = "Zigbee";

    public bool Enabled { get; set; }

    /// <summary>zigbee2mqtt's base_topic.</summary>
    public string BaseTopic { get; set; } = "zigbee2mqtt";

    /// <summary>The login zigbee2mqtt connects with; the hive creates it (dynamic security) when Enabled and a password is set.</summary>
    public string MqttUser { get; set; } = "zigbee2mqtt";

    public string? MqttPassword { get; set; }
}

/// <summary>A device zigbee2mqtt knows (from its retained bridge/devices list).</summary>
public sealed record ZigbeeDevice(
    string Ieee, string FriendlyName, string Type, string? Vendor, string? Model, string? Description,
    bool Supported, bool InterviewCompleted);

/// <summary>What the hive knows about the zigbee2mqtt bridge: whether it is up, and the devices it has paired.</summary>
public sealed class ZigbeeCatalog
{
    private volatile string _bridge = "unknown";
    private volatile IReadOnlyList<ZigbeeDevice> _devices = [];
    private long _permitUntilTicks;

    /// <summary>"online", "offline" or "unknown" (nothing heard yet).</summary>
    public string Bridge => _bridge;

    public IReadOnlyList<ZigbeeDevice> Devices => _devices;

    public void SetBridge(string state) => _bridge = state;

    public void SetDevices(IReadOnlyList<ZigbeeDevice> devices) => _devices = devices;

    /// <summary>When pairing mode ends (null: it is not on).</summary>
    public DateTimeOffset? PermitJoinUntil(TimeProvider time)
    {
        var until = new DateTimeOffset(Interlocked.Read(ref _permitUntilTicks), TimeSpan.Zero);
        return Interlocked.Read(ref _permitUntilTicks) > 0 && until > time.GetUtcNow() ? until : null;
    }

    public void SetPermitJoin(DateTimeOffset? until) => Interlocked.Exchange(ref _permitUntilTicks, until?.UtcTicks ?? 0);
}

/// <summary>Topic names under zigbee2mqtt's base topic.</summary>
public static class ZigbeeTopics
{
    public static string Bridge(string baseTopic, string what) => $"{baseTopic}/bridge/{what}";

    public static string Set(string baseTopic, string friendlyName) => $"{baseTopic}/{friendlyName}/set";

    public static string PermitJoin(string baseTopic) => $"{baseTopic}/bridge/request/permit_join";

    public static string Rename(string baseTopic) => $"{baseTopic}/bridge/request/device/rename";
}

/// <summary>One MQTT message to zigbee2mqtt: the topic relative to the device or bridge, and its JSON payload.</summary>
public sealed record ZigbeeCall(string Payload);

/// <summary>What to send for a hive command to a Zigbee device, and which answer confirms it ("power").</summary>
public sealed record ZigbeePlan(ZigbeeCall Call, string Confirms);

/// <summary>Turns our commands into zigbee2mqtt's. A plug's relay: <c>{"state":"ON"}</c> on the device's <c>/set</c>.</summary>
public static class ZigbeeCommands
{
    /// <summary>Null when a Zigbee device cannot do it. Timed switch-on (for_s) is refused: nothing here would switch it off if the hive were gone.</summary>
    public static ZigbeePlan? Plan(string name, JsonObject? payload)
    {
        if (name != "relay" || payload?["for_s"] is { } f && f.GetValueKind() == System.Text.Json.JsonValueKind.Number && f.GetValue<int>() > 0)
            return null;
        var state = payload?["set"]?.GetValue<string>() switch
        {
            "on" => "ON",
            "off" => "OFF",
            "toggle" => "TOGGLE",
            _ => null,
        };
        return state is null ? null : new ZigbeePlan(new ZigbeeCall(new JsonObject { ["state"] = state }.ToJsonString()), "power");
    }
}

/// <summary>Registers the Zigbee options and catalog; both modules that need them call it.</summary>
public static class ZigbeeRegistration
{
    public static IServiceCollection AddZigbeeBridge(this IServiceCollection services)
    {
        services.AddOptions<ZigbeeOptions>().BindConfiguration(ZigbeeOptions.Section);
        services.TryAddSingleton<ZigbeeCatalog>();
        services.TryAddSingleton<TasmotaPending>();
        return services;
    }
}

/// <summary>Gives zigbee2mqtt its login once the broker is reachable, so it needs no hand-made mosquitto settings.</summary>
public sealed class ZigbeeLogin(MqttGateway mqtt, DynamicSecurity dynsec, IOptions<ZigbeeOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || string.IsNullOrEmpty(options.Value.MqttPassword))
            return Task.CompletedTask;
        mqtt.Connected += Provision;
        return mqtt.IsConnected ? Provision(cancellationToken) : Task.CompletedTask;
    }

    private Task Provision(CancellationToken ct) =>
        dynsec.ProvisionZigbeeBridgeAsync(options.Value.MqttUser, options.Value.MqttPassword!, options.Value.BaseTopic, ct);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        mqtt.Connected -= Provision;
        return Task.CompletedTask;
    }
}
