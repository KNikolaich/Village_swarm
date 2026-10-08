namespace Hive.Domain;

/// <summary>A Telegram chat allowed to talk to the bot, linked to a user with a one-time code (spec 9.4).</summary>
public class TgLink
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public long ChatId { get; set; }
    public string? Username { get; set; }
    public DateTimeOffset LinkedAt { get; set; }

    /// <summary>Lowest severity sent to this chat (spec 6.7): warn by default, alarm for "only real alarms".</summary>
    public EventSeverity NotifyLevel { get; set; } = EventSeverity.Warn;

    /// <summary>/mute: warn messages are dropped until then; alarm and critical still come.</summary>
    public DateTimeOffset? MutedUntil { get; set; }
}

/// <summary>One-time code shown in the web UI; the user sends <c>/link CODE</c> to the bot.</summary>
public class TgLinkCode
{
    public required string Code { get; set; }
    public int UserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public enum NotificationKind
{
    /// <summary>Alarm text with inline buttons.</summary>
    Event,

    /// <summary>Photos of an event, sent as a reply to its text once uploaded (spec 9.5).</summary>
    EventPhotos,

    /// <summary>Photos of a /photo request.</summary>
    Snapshot,

    /// <summary>Plain text (repeats of critical alarms, system messages).</summary>
    Text,
}

public enum NotificationStatus
{
    Pending,
    Sent,
    Failed,
    Skipped,
}

/// <summary>Outbox row (spec 6.3 NotificationOutbox): nothing is lost while the internet is down.</summary>
public class Notification
{
    public long Id { get; set; }
    public string Channel { get; set; } = "tg";
    public required string Target { get; set; }
    public NotificationKind Kind { get; set; }
    public string? EventId { get; set; }
    public string? DeviceIds { get; set; }
    public required string Text { get; set; }
    public bool Silent { get; set; }
    public string? DedupeKey { get; set; }

    /// <summary>Same alarm again within the grouping window: the sent message is edited to "×N".</summary>
    public int RepeatCount { get; set; } = 1;

    public bool EditPending { get; set; }
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Photos are waited for until then (spec 9.5: up to 15 s), then sent as they are.</summary>
    public DateTimeOffset? WaitUntil { get; set; }

    /// <summary>Photos expected (event photos, or one per camera for a snapshot).</summary>
    public int ExpectedPhotos { get; set; }

    public DateTimeOffset? SentAt { get; set; }
    public long? MessageId { get; set; }
    public long? ReplyToMessageId { get; set; }
    public string? Error { get; set; }
}
