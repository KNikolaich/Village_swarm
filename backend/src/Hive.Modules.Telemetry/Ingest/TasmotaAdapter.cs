using System.Text;
using System.Text.Json;
using Hive.Contracts;
using Hive.Domain;
using Hive.Infrastructure.Mqtt;
using Microsoft.Extensions.Logging;

namespace Hive.Modules.Telemetry.Ingest;

/// <summary>
/// Adapter for Tasmota and OpenBeken devices (contracts/tasmota.md): <c>tele/{id}/…</c> and <c>stat/{id}/…</c> become the
/// same ingest items as our own hornets' messages, so the rest of the hive does not tell them apart.
/// </summary>
public sealed class TasmotaAdapter(IngestQueue queue, TasmotaPending pending, ILogger<TasmotaAdapter> logger) : IMqttMessageHandler
{
    public IReadOnlyList<string> TopicFilters => [TasmotaTopics.AllTele, TasmotaTopics.AllStat];

    public ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt)
    {
        try
        {
            foreach (var item in Parse(topic, payload.Span, receivedAt, pending))
                queue.Enqueue(item);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Dropped {Topic}: invalid payload: {Error}", topic, ex.Message);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Pure parsing step, public for tests. Topics the hive does not use give an empty list.</summary>
    public static IReadOnlyList<IngestItem> Parse(string topic, ReadOnlySpan<byte> payload, DateTimeOffset receivedAt, TasmotaPending pending)
    {
        if (TasmotaTopics.TryParse(topic) is not var (area, id, kind) || payload.IsEmpty)
            return [];
        var text = Encoding.UTF8.GetString(payload);

        if (area == "tele")
            return kind switch
            {
                "LWT" => Lwt(id, text, receivedAt),
                "STATE" => State(id, JsonDocument.Parse(text), receivedAt),
                "SENSOR" => Sensor(id, JsonDocument.Parse(text), receivedAt),
                "RESULT" => IrReceived(id, JsonDocument.Parse(text), receivedAt),
                _ => [],
            };

        // stat/{id}/POWER or POWER2: the plain answer to a switch
        if (kind.StartsWith("POWER", StringComparison.Ordinal) && (kind.Length == 5 || int.TryParse(kind.AsSpan(5), out _)))
            return Power(id, kind, text, receivedAt, pending);
        if (kind == "RESULT" && text.Contains("\"Restart\"", StringComparison.Ordinal))
            return Ack(id, "restart", receivedAt, pending);
        if (kind == "RESULT" && text.Contains("\"IRSend\"", StringComparison.Ordinal))
            return Ack(id, "ir", receivedAt, pending);
        return [];
    }

    private static IReadOnlyList<IngestItem> Lwt(string id, string text, DateTimeOffset receivedAt) => text switch
    {
        "Online" => [new DeviceStatusChanged(id, true, receivedAt)],
        "Offline" => [new DeviceStatusChanged(id, false, receivedAt)],
        _ => [],
    };

    /// <summary>tele STATE: uptime, memory, Wi-Fi signal and where the relays are. Every TelePeriod seconds, so it also says "alive".</summary>
    private static IReadOnlyList<IngestItem> State(string id, JsonDocument doc, DateTimeOffset receivedAt)
    {
        var root = doc.RootElement;
        var items = new List<IngestItem>();
        var signal = root.TryGetProperty("Wifi", out var wifi) && wifi.TryGetProperty("Signal", out var s) && s.TryGetInt32(out var dbm) ? dbm : 0;
        items.Add(new DeviceHealthReceived(id, NewId(receivedAt), receivedAt, receivedAt,
            Long(root, "UptimeSec"), signal, Long(root, "Heap") * 1024, null, "", false, (int)Long(root, "MqttCount")));

        var state = new Dictionary<string, string>();
        foreach (var p in root.EnumerateObject())
            if (IsPower(p.Name) && p.Value.ValueKind == JsonValueKind.String)
                state[Channel(p.Name)] = p.Value.GetString()!.ToLowerInvariant();
        if (state.Count > 0)
            items.Add(new DeviceStateReceived(id, NewId(receivedAt), receivedAt, receivedAt, JsonSerializer.SerializeToDocument(state), Merge: true));
        return items;
    }

    /// <summary>tele SENSOR: every sensor block is named after the sensor (AM2301, DS18B20, ENERGY …).</summary>
    private static IReadOnlyList<IngestItem> Sensor(string id, JsonDocument doc, DateTimeOffset receivedAt)
    {
        var values = new List<(string, double)>();
        var temperatures = 0;
        foreach (var block in doc.RootElement.EnumerateObject())
        {
            if (block.Value.ValueKind != JsonValueKind.Object)
                continue;
            if (block.Name == "ENERGY")
            {
                Add(values, block.Value, "Power", "power_w");
                Add(values, block.Value, "Voltage", "voltage_v");
                Add(values, block.Value, "Current", "current_a");
                Add(values, block.Value, "Today", "energy_today_kwh");
                Add(values, block.Value, "Total", "energy_total_kwh");
                continue;
            }
            if (block.Value.TryGetProperty("Temperature", out _))
                Add(values, block.Value, "Temperature", ++temperatures == 1 ? "temperature" : $"temperature{temperatures}");
            Add(values, block.Value, "Humidity", "humidity");
            Add(values, block.Value, "Pressure", "pressure_hpa");
        }
        return values.Count == 0 ? [] : [new TelemetryReceived(id, NewId(receivedAt), receivedAt, receivedAt, values)];
    }

    /// <summary>
    /// An infrared code the device saw (a remote pointed at it): kept as the state "ir_last" ("NEC 32 0x02FD807F") so that
    /// a code can be learned by pressing the remote's button, without filling the event feed.
    /// </summary>
    private static IReadOnlyList<IngestItem> IrReceived(string id, JsonDocument doc, DateTimeOffset receivedAt)
    {
        if (!doc.RootElement.TryGetProperty("IrReceived", out var ir) || ir.ValueKind != JsonValueKind.Object)
            return [];
        var protocol = ir.TryGetProperty("Protocol", out var p) ? p.GetString() : null;
        var bits = ir.TryGetProperty("Bits", out var b) && b.TryGetInt32(out var n) ? n : 0;
        var data = ir.TryGetProperty("Data", out var d) ? d.ValueKind == JsonValueKind.String ? d.GetString() : d.GetRawText() : null;
        if (protocol is null || data is null)
            return [];
        var state = new Dictionary<string, string> { ["ir_last"] = $"{protocol} {bits} {data}" };
        return [new DeviceStateReceived(id, NewId(receivedAt), receivedAt, receivedAt, JsonSerializer.SerializeToDocument(state), Merge: true)];
    }

    private static IReadOnlyList<IngestItem> Power(string id, string kind, string text, DateTimeOffset receivedAt, TasmotaPending pending)
    {
        if (text is not ("ON" or "OFF"))
            return [];
        var state = new Dictionary<string, string> { [Channel(kind)] = text.ToLowerInvariant() };
        var items = new List<IngestItem> { new DeviceStateReceived(id, NewId(receivedAt), receivedAt, receivedAt, JsonSerializer.SerializeToDocument(state), Merge: true) };
        items.AddRange(Ack(id, "power", receivedAt, pending));
        return items;
    }

    private static IReadOnlyList<IngestItem> Ack(string id, string kind, DateTimeOffset receivedAt, TasmotaPending pending) =>
        pending.Take(id, kind) is { } cid
            ? [new CommandAckReceived(id, NewId(receivedAt), receivedAt, receivedAt, cid, "ok", JsonDocument.Parse("""{"via":"tasmota"}"""))]
            : [];

    private static bool IsPower(string name) =>
        name.StartsWith("POWER", StringComparison.Ordinal) && (name.Length == 5 || int.TryParse(name.AsSpan(5), out _));

    /// <summary>POWER is the only relay (our "relay"), POWER2 is the second one ("relay2").</summary>
    private static string Channel(string power) => power.Length == 5 ? "relay" : $"relay{power[5..]}";

    private static void Add(List<(string, double)> values, JsonElement block, string property, string metric)
    {
        if (block.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number)
            values.Add((metric, v.GetDouble()));
    }

    private static long Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string NewId(DateTimeOffset at) => Ulid.New(at);
}
