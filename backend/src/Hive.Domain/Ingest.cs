using System.Text.Json;

namespace Hive.Domain;

/// <summary>
/// Normalized message from any device adapter (native hornet, later Tasmota, Zigbee2MQTT; spec 4.6).
/// <see cref="MessageId"/> is used for deduplication; null when the source has no ids (plain status).
/// <see cref="Ts"/> is already the effective time: the device clock, or receive time when it was untrustworthy.
/// </summary>
public abstract record IngestItem(string DeviceId, string? MessageId, DateTimeOffset Ts, DateTimeOffset ReceivedAt);

public sealed record DeviceStatusChanged(string DeviceId, bool Online, DateTimeOffset ReceivedAt)
    : IngestItem(DeviceId, null, ReceivedAt, ReceivedAt);

public sealed record DeviceInfoReceived(
    string DeviceId, string MessageId, DateTimeOffset Ts, DateTimeOffset ReceivedAt,
    string TypeCode, string Hw, string Fw, string Mac, string? Ip, int Boot, JsonDocument Info)
    : IngestItem(DeviceId, MessageId, Ts, ReceivedAt);

public sealed record DeviceHealthReceived(
    string DeviceId, string MessageId, DateTimeOffset Ts, DateTimeOffset ReceivedAt,
    long UptimeS, int Rssi, long HeapFree, double? Vbat, string ResetReason, bool ClockSkew, int Boot)
    : IngestItem(DeviceId, MessageId, Ts, ReceivedAt);

public sealed record TelemetryReceived(
    string DeviceId, string MessageId, DateTimeOffset Ts, DateTimeOffset ReceivedAt,
    IReadOnlyList<(string Metric, double Value)> Values)
    : IngestItem(DeviceId, MessageId, Ts, ReceivedAt);

public sealed record DeviceStateReceived(
    string DeviceId, string MessageId, DateTimeOffset Ts, DateTimeOffset ReceivedAt, JsonDocument State)
    : IngestItem(DeviceId, MessageId, Ts, ReceivedAt);

public sealed record DeviceEventReceived(
    string DeviceId, string MessageId, DateTimeOffset Ts, DateTimeOffset ReceivedAt,
    string Type, EventSeverity Severity, bool? Armed, JsonDocument Payload)
    : IngestItem(DeviceId, MessageId, Ts, ReceivedAt);

public sealed record DeviceLogReceived(
    string DeviceId, string MessageId, DateTimeOffset Ts, DateTimeOffset ReceivedAt,
    string Level, string Msg, JsonDocument? Ctx)
    : IngestItem(DeviceId, MessageId, Ts, ReceivedAt);
