using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hive.Contracts.Messages;

// Messages a hornet publishes (dev -> hive). Schemas: contracts/schemas/*.schema.json.

/// <summary>Envelope every hornet message carries (spec 4.3).</summary>
public abstract record HornetMessage
{
    [JsonPropertyOrder(-5)]
    public int V { get; init; } = 1;

    /// <summary>Message ULID, the deduplication key.</summary>
    [JsonPropertyOrder(-4)]
    public required string Id { get; init; }

    /// <summary>Unix ms by the hornet clock; 0 when not synced.</summary>
    [JsonPropertyOrder(-3)]
    public required long Ts { get; init; }

    [JsonPropertyOrder(-2)]
    public required long Seq { get; init; }

    [JsonPropertyOrder(-1)]
    public required int Boot { get; init; }
}

/// <summary>vs/v1/dev/{id}/info</summary>
public sealed record InfoMessage : HornetMessage
{
    public required string Type { get; init; }
    public required string Hw { get; init; }
    public required string Fw { get; init; }
    public required string Mac { get; init; }
    public string? Ip { get; init; }
    public required IReadOnlyList<string> Caps { get; init; }
    public required IReadOnlyList<string> Metrics { get; init; }
    public required IReadOnlyList<string> Commands { get; init; }
}

/// <summary>vs/v1/dev/{id}/health</summary>
public sealed record HealthMessage : HornetMessage
{
    public required long UptimeS { get; init; }
    public required int Rssi { get; init; }
    public required long HeapFree { get; init; }
    public long? PsramFree { get; init; }
    public double? Vbat { get; init; }
    public required string ResetReason { get; init; }
    public int? Outbox { get; init; }
    public BrokerNode? Broker { get; init; }
}

/// <summary>vs/v1/dev/{id}/tele/{metric}</summary>
public sealed record TeleMessage : HornetMessage
{
    public required double Value { get; init; }
    public string? Unit { get; init; }
    public string? Sensor { get; init; }
}

/// <summary>vs/v1/dev/{id}/tele/batch</summary>
public sealed record TeleBatchMessage : HornetMessage
{
    public required IReadOnlyList<TeleBatchItem> Items { get; init; }
}

public sealed record TeleBatchItem
{
    /// <summary>Metric name.</summary>
    public required string M { get; init; }

    /// <summary>Value.</summary>
    public required double V { get; init; }

    public string? Unit { get; init; }
}

/// <summary>vs/v1/dev/{id}/state</summary>
public sealed record StateMessage : HornetMessage
{
    public required IReadOnlyDictionary<string, JsonElement> State { get; init; }
}

/// <summary>vs/v1/dev/{id}/event/{type}. Unknown event types keep their fields in <see cref="Extra"/>.</summary>
public record EventMessage : HornetMessage
{
    public Severity? Severity { get; init; }
    public bool? Armed { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>vs/v1/dev/{id}/event/motion</summary>
public sealed record MotionEvent : EventMessage
{
    public required MotionSource Source { get; init; }
    public required IReadOnlyList<string> Photos { get; init; }
    public required PhotoStatus PhotoStatus { get; init; }
}

/// <summary>vs/v1/dev/{id}/event/config_applied</summary>
public sealed record ConfigAppliedEvent : EventMessage
{
    public required int Rev { get; init; }
}

/// <summary>vs/v1/dev/{id}/log</summary>
public sealed record LogMessage : HornetMessage
{
    public required HornetLogLevel Level { get; init; }
    public required string Msg { get; init; }
    public JsonElement? Ctx { get; init; }
}

/// <summary>vs/v1/dev/{id}/cmd/{name}/ack</summary>
public sealed record AckMessage : HornetMessage
{
    public required string Cid { get; init; }
    public required AckStatus Status { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? State { get; init; }
    public string? Msg { get; init; }
}

public enum Severity { Info, Warn, Alarm, Critical }

public enum MotionSource { Pir, Mmwave, Camera }

public enum PhotoStatus { None, Uploading, Buffered }

public enum HornetLogLevel { Debug, Info, Warn, Error }

public enum AckStatus { Ok, Rejected, Expired, Error, Unsupported }

public enum BrokerNode { Home, City }
