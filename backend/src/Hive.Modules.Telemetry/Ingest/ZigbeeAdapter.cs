using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Contracts;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure.Mqtt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Telemetry.Ingest;

/// <summary>
/// Adapter for zigbee2mqtt (contracts/zigbee.md): <c>{base}/{friendly_name}</c> reports become the same ingest items as a
/// hornet's, so a Zigbee sensor is one more device in the hive. The friendly name is the device_id. The bridge's own
/// topics feed the catalog of paired devices.
/// </summary>
public sealed class ZigbeeAdapter(
    IngestQueue queue,
    ZigbeeCatalog catalog,
    TasmotaPending pending,
    IOptions<ZigbeeOptions> options,
    ILogger<ZigbeeAdapter> logger) : IMqttMessageHandler
{
    private readonly ConcurrentDictionary<(string Device, string Field), bool> _last = new();

    public IReadOnlyList<string> TopicFilters => options.Value.Enabled ? [$"{options.Value.BaseTopic}/#"] : [];

    public ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt)
    {
        var prefix = options.Value.BaseTopic + "/";
        if (!topic.StartsWith(prefix, StringComparison.Ordinal))
            return ValueTask.CompletedTask;
        try
        {
            foreach (var item in Handle(topic[prefix.Length..], payload.Span, receivedAt))
                queue.Enqueue(item);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Dropped {Topic}: invalid payload: {Error}", topic, ex.Message);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>The parsing step, public for tests: a topic below the base topic, its payload. Bridge topics update the catalog.</summary>
    public IReadOnlyList<IngestItem> Handle(string relativeTopic, ReadOnlySpan<byte> payload, DateTimeOffset receivedAt)
    {
        var parts = relativeTopic.Split('/');
        if (payload.IsEmpty)
            return [];
        var text = Encoding.UTF8.GetString(payload);

        if (parts[0] == "bridge")
        {
            Bridge(parts, text);
            return [];
        }
        if (!Topics.IsValidDeviceId(parts[0]))
            return [];
        var id = parts[0];
        if (parts.Length == 2 && parts[1] == "availability")
            return Availability(id, text, receivedAt);
        return parts.Length == 1 ? Report(id, JsonDocument.Parse(text).RootElement, receivedAt) : [];
    }

    private void Bridge(string[] parts, string text)
    {
        if (parts.Length == 2 && parts[1] == "state")
            catalog.SetBridge(StateOf(text));
        else if (parts.Length == 2 && parts[1] == "devices")
            catalog.SetDevices(Devices(JsonDocument.Parse(text).RootElement));
    }

    /// <summary>"online" or "offline", as plain text (zigbee2mqtt 1.x) or {"state": "..."} (2.x).</summary>
    private static string StateOf(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{'))
            return trimmed is "online" or "offline" ? trimmed : "unknown";
        var state = JsonDocument.Parse(trimmed).RootElement.TryGetProperty("state", out var s) ? s.GetString() : null;
        return state is "online" or "offline" ? state : "unknown";
    }

    public static IReadOnlyList<ZigbeeDevice> Devices(JsonElement array)
    {
        var devices = new List<ZigbeeDevice>();
        if (array.ValueKind != JsonValueKind.Array)
            return devices;
        foreach (var d in array.EnumerateArray())
        {
            var type = Text(d, "type") ?? "";
            if (type == "Coordinator" || Text(d, "ieee_address") is not { } ieee)
                continue;
            JsonElement definition = default;
            var has = d.TryGetProperty("definition", out definition) && definition.ValueKind == JsonValueKind.Object;
            devices.Add(new ZigbeeDevice(
                ieee, Text(d, "friendly_name") ?? ieee, type,
                has ? Text(definition, "vendor") : null, has ? Text(definition, "model") : null, has ? Text(definition, "description") : null,
                d.TryGetProperty("supported", out var sup) && sup.ValueKind == JsonValueKind.True,
                d.TryGetProperty("interview_completed", out var ic) && ic.ValueKind == JsonValueKind.True));
        }
        return devices;
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<IngestItem> Availability(string id, string text, DateTimeOffset receivedAt) => StateOf(text) switch
    {
        "online" => [new DeviceStatusChanged(id, true, receivedAt)],
        "offline" => [new DeviceStatusChanged(id, false, receivedAt)],
        _ => [],
    };

    private IReadOnlyList<IngestItem> Report(string id, JsonElement root, DateTimeOffset at)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return [];
        var items = new List<IngestItem> { new DeviceStatusChanged(id, true, at) };

        var values = new List<(string, double)>();
        void Number(string field, string metric, double scale = 1)
        {
            if (root.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number)
                values.Add((metric, Math.Round(v.GetDouble() * scale, 3)));
        }
        Number("temperature", "temperature");
        Number("humidity", "humidity");
        Number("pressure", "pressure_hpa");
        Number("battery", "battery_pct");
        Number("linkquality", "linkquality");
        Number("illuminance_lux", "illuminance_lux");
        Number("illuminance", "illuminance_lux");
        Number("power", "power_w");
        Number("current", "current_a");
        Number("energy", "energy_kwh");
        // "voltage" is millivolts for a battery sensor and volts for a mains plug.
        if (root.TryGetProperty("voltage", out var voltage) && voltage.ValueKind == JsonValueKind.Number)
            values.Add(voltage.GetDouble() >= 1000 ? ("battery_voltage", Math.Round(voltage.GetDouble() / 1000, 3)) : ("voltage_v", voltage.GetDouble()));
        if (values.Count > 0)
            items.Add(new TelemetryReceived(id, NewId(at), at, at, values));

        // Relays: state, or state_l1, state_l2 on several-gang devices.
        var relays = new Dictionary<string, string>();
        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String)
                continue;
            var name = p.Name == "state" ? "relay" : p.Name.StartsWith("state_l", StringComparison.Ordinal) && int.TryParse(p.Name.AsSpan(7), out var n) ? $"relay{n}" : null;
            if (name is not null && p.Value.GetString() is "ON" or "OFF")
                relays[name] = p.Value.GetString()!.ToLowerInvariant();
        }
        if (relays.Count > 0)
        {
            items.Add(new DeviceStateReceived(id, NewId(at), at, at, JsonSerializer.SerializeToDocument(relays), Merge: true));
            if (pending.Take(id, "power") is { } cid)
                items.Add(new CommandAckReceived(id, NewId(at), at, at, cid, "ok", JsonDocument.Parse("""{"via":"zigbee2mqtt"}""")));
        }

        // Binary sensors: an event when the value changes to the one that matters, not on every report that repeats it.
        Trigger(items, id, root, at, "occupancy", true, "motion", EventSeverity.Info);
        Trigger(items, id, root, at, "contact", false, "door_open", EventSeverity.Info);
        Trigger(items, id, root, at, "water_leak", true, "leak", EventSeverity.Critical);
        Trigger(items, id, root, at, "smoke", true, "smoke", EventSeverity.Critical);
        Trigger(items, id, root, at, "tamper", true, "tamper", EventSeverity.Warn);
        return items;
    }

    private void Trigger(List<IngestItem> items, string id, JsonElement root, DateTimeOffset at, string field, bool trigger, string type, EventSeverity severity)
    {
        if (!root.TryGetProperty(field, out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return;
        var now = v.ValueKind == JsonValueKind.True;
        var before = _last.TryGetValue((id, field), out var b) ? (bool?)b : null;
        _last[(id, field)] = now;
        if (now != trigger || before == trigger)
            return;
        var payload = new JsonObject { ["v"] = 1, ["source"] = "zigbee2mqtt", ["field"] = field, ["severity"] = severity.ToString().ToLowerInvariant() };
        items.Add(new DeviceEventReceived(id, NewId(at), at, at, type, severity, null, JsonDocument.Parse(payload.ToJsonString())));
    }

    private static string NewId(DateTimeOffset at) => Ulid.New(at);
}
