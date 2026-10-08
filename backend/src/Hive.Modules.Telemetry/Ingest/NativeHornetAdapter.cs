using System.Text;
using System.Text.Json;
using Hive.Contracts;
using Hive.Contracts.Messages;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure.Mqtt;
using Microsoft.Extensions.Logging;

namespace Hive.Modules.Telemetry.Ingest;

/// <summary>Adapter for our own hornets: <c>vs/v1/dev/{id}/...</c> payloads per contracts/ become ingest items (spec 4.6).</summary>
public sealed class NativeHornetAdapter(IngestQueue queue, ILogger<NativeHornetAdapter> logger) : IMqttMessageHandler
{
    public IReadOnlyList<string> TopicFilters => [Topics.AllDevices];

    public ValueTask HandleAsync(string topic, ReadOnlyMemory<byte> payload, bool retained, DateTimeOffset receivedAt)
    {
        var item = Parse(topic, payload.Span, receivedAt, out var error);
        if (error is not null)
            logger.LogWarning("Dropped {Topic}: {Error}", topic, error);
        if (item is not null)
            queue.Enqueue(item);
        return ValueTask.CompletedTask;
    }

    public static DateTimeOffset EffectiveTs(long ts, DateTimeOffset receivedAt) => MessageTime.Effective(ts, receivedAt);

    public static bool IsClockSkewed(long ts, DateTimeOffset receivedAt) => MessageTime.IsSkewed(ts, receivedAt);

    /// <summary>Pure parsing step, public for tests. Returns null for topics the hive does not ingest.</summary>
    public static IngestItem? Parse(string topic, ReadOnlySpan<byte> payload, DateTimeOffset receivedAt, out string? error)
    {
        error = null;
        var parts = topic.Split('/');
        var deviceId = Topics.TryGetDeviceId(topic);
        if (deviceId is null)
        {
            error = "not a device topic or invalid device_id";
            return null;
        }
        if (payload.IsEmpty)
            return null; // retained message cleared

        try
        {
            return (parts[4], parts.Length) switch
            {
                ("status", 5) => Status(deviceId, Encoding.UTF8.GetString(payload), receivedAt, out error),
                ("info", 5) => Info(deviceId, JsonSerializer.Deserialize<InfoMessage>(payload, ContractJson.Options)!, receivedAt),
                ("health", 5) => Health(deviceId, JsonSerializer.Deserialize<HealthMessage>(payload, ContractJson.Options)!, receivedAt),
                ("tele", 6) when parts[5] == "batch" => TeleBatch(deviceId, JsonSerializer.Deserialize<TeleBatchMessage>(payload, ContractJson.Options)!, receivedAt),
                ("tele", 6) => Tele(deviceId, parts[5], JsonSerializer.Deserialize<TeleMessage>(payload, ContractJson.Options)!, receivedAt),
                ("state", 5) => State(deviceId, JsonSerializer.Deserialize<StateMessage>(payload, ContractJson.Options)!, receivedAt),
                ("event", 6) => Event(deviceId, parts[5], payload, receivedAt),
                ("log", 5) => Log(deviceId, JsonSerializer.Deserialize<LogMessage>(payload, ContractJson.Options)!, receivedAt),
                ("cmd", _) => null, // commands are ours; acks are handled by CommandDispatcher (later step)
                ("config", 5) => null, // published by the hive itself
                _ => Unknown(out error),
            };
        }
        catch (JsonException ex)
        {
            error = $"invalid payload: {ex.Message}";
            return null;
        }
    }

    private static IngestItem? Status(string deviceId, string payload, DateTimeOffset receivedAt, out string? error)
    {
        error = null;
        switch (payload)
        {
            case "online": return new DeviceStatusChanged(deviceId, true, receivedAt);
            case "offline": return new DeviceStatusChanged(deviceId, false, receivedAt);
            default:
                error = $"unknown status '{payload}'";
                return null;
        }
    }

    private static DeviceInfoReceived Info(string deviceId, InfoMessage m, DateTimeOffset receivedAt) =>
        new(deviceId, m.Id, EffectiveTs(m.Ts, receivedAt), receivedAt, m.Type, m.Hw, m.Fw, m.Mac, m.Ip, m.Boot,
            JsonSerializer.SerializeToDocument(new { m.Caps, m.Metrics, m.Commands }, ContractJson.Options));

    private static DeviceHealthReceived Health(string deviceId, HealthMessage m, DateTimeOffset receivedAt)
    {
        var skewed = IsClockSkewed(m.Ts, receivedAt);
        return new(deviceId, m.Id, skewed ? receivedAt : DateTimeOffset.FromUnixTimeMilliseconds(m.Ts), receivedAt,
            m.UptimeS, m.Rssi, m.HeapFree, m.Vbat, m.ResetReason, skewed, m.Boot);
    }

    private static TelemetryReceived Tele(string deviceId, string metric, TeleMessage m, DateTimeOffset receivedAt) =>
        new(deviceId, m.Id, EffectiveTs(m.Ts, receivedAt), receivedAt, [(metric, m.Value)]);

    private static TelemetryReceived TeleBatch(string deviceId, TeleBatchMessage m, DateTimeOffset receivedAt) =>
        new(deviceId, m.Id, EffectiveTs(m.Ts, receivedAt), receivedAt,
            m.Items.Where(i => Topics.IsValidSegment(i.M)).Select(i => (i.M, i.V)).ToList());

    private static DeviceStateReceived State(string deviceId, StateMessage m, DateTimeOffset receivedAt) =>
        new(deviceId, m.Id, EffectiveTs(m.Ts, receivedAt), receivedAt, JsonSerializer.SerializeToDocument(m.State));

    private static DeviceEventReceived? Event(string deviceId, string type, ReadOnlySpan<byte> payload, DateTimeOffset receivedAt)
    {
        var m = JsonSerializer.Deserialize<EventMessage>(payload, ContractJson.Options)!;
        var severity = m.Severity is { } s ? (EventSeverity)(int)s : DefaultSeverity(type, m.Armed);
        return new DeviceEventReceived(deviceId, m.Id, EffectiveTs(m.Ts, receivedAt), receivedAt,
            type, severity, m.Armed, JsonDocument.Parse(payload.ToArray()));
    }

    /// <summary>Severity when the hornet does not suggest one. The rules engine may raise it later.</summary>
    public static EventSeverity DefaultSeverity(string type, bool? armed) => type switch
    {
        "motion" or "door_open" => armed == true ? EventSeverity.Alarm : EventSeverity.Info,
        "leak" => EventSeverity.Critical,
        "failsafe" or "photo_failed" or "tamper" => EventSeverity.Warn,
        _ => EventSeverity.Info,
    };

    private static DeviceLogReceived Log(string deviceId, LogMessage m, DateTimeOffset receivedAt) =>
        new(deviceId, m.Id, EffectiveTs(m.Ts, receivedAt), receivedAt, m.Level.ToString().ToLowerInvariant(), m.Msg,
            m.Ctx is { } ctx ? JsonDocument.Parse(ctx.GetRawText()) : null);

    private static IngestItem? Unknown(out string? error)
    {
        error = "unknown topic kind";
        return null;
    }
}
