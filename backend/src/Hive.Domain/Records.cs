using System.Text.Json;

namespace Hive.Domain;

public enum EventSeverity
{
    Info,
    Warn,
    Alarm,
    Critical,
}

public enum HiveNode
{
    Home,
    City,
}

/// <summary>One telemetry sample. Stored in the monthly-partitioned <c>telemetry</c> table.</summary>
public class TelemetryPoint
{
    public required string DeviceId { get; set; }
    public required string Metric { get; set; }
    public DateTimeOffset Ts { get; set; }
    public double Value { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>Device event: motion, leak, failsafe... <see cref="Id"/> is the message ULID, so replays dedupe.</summary>
public class DeviceEvent
{
    public required string Id { get; set; }
    public required string DeviceId { get; set; }
    public required string Type { get; set; }
    public EventSeverity Severity { get; set; }
    public DateTimeOffset Ts { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public required JsonDocument Payload { get; set; }
    public bool? Armed { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public HiveNode ViaNode { get; set; } = HiveNode.Home;
}

public class DeviceLog
{
    public long Id { get; set; }
    public required string DeviceId { get; set; }
    public DateTimeOffset Ts { get; set; }
    public required string Level { get; set; }
    public required string Msg { get; set; }
    public JsonDocument? Ctx { get; set; }
}

public class DeviceHealth
{
    public long Id { get; set; }
    public required string DeviceId { get; set; }
    public DateTimeOffset Ts { get; set; }
    public long UptimeS { get; set; }
    public int Rssi { get; set; }
    public long HeapFree { get; set; }
    public double? Vbat { get; set; }
    public required string ResetReason { get; set; }

    /// <summary>The hornet clock was unsynced or more than 5 min off; <see cref="Ts"/> is the receive time.</summary>
    public bool ClockSkew { get; set; }
}
