using System.Text.Json;

namespace Hive.Domain;

public enum CommandStatus
{
    Sent,
    Ok,
    Rejected,
    Expired,
    Error,
    Unsupported,
    Timeout,
}

/// <summary>A command to a hornet (spec 6.4 commands). <see cref="Cid"/> is the ULID idempotency key.</summary>
public class DeviceCommand
{
    public required string Cid { get; set; }
    public required string DeviceId { get; set; }
    public required string Name { get; set; }
    public required JsonDocument Payload { get; set; }

    /// <summary>user:x | rule:y | tg:z | system:w</summary>
    public required string IssuedBy { get; set; }

    public DateTimeOffset IssuedAt { get; set; }
    public int TtlS { get; set; }
    public CommandStatus Status { get; set; } = CommandStatus.Sent;
    public DateTimeOffset? AckAt { get; set; }
    public JsonDocument? AckPayload { get; set; }
}

/// <summary>House-wide mode, e.g. "armed" (spec 6.4 modes).</summary>
public class Mode
{
    public const string Armed = "armed";

    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Who did what: arming, disarming, Telegram links, rejected chats (spec 6.4 audit_log).</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset Ts { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public string? Target { get; set; }
    public JsonDocument? Details { get; set; }
}

public sealed record CommandAckReceived(
    string DeviceId, string MessageId, DateTimeOffset Ts, DateTimeOffset ReceivedAt,
    string Cid, string Status, JsonDocument Payload)
    : IngestItem(DeviceId, MessageId, Ts, ReceivedAt);
