namespace Hive.Domain;

public enum MediaKind
{
    Photo,
    Timelapse,
    Snapshot,
}

/// <summary>
/// Photo or video stored as a file under the media root; metadata here (spec 7.1).
/// <see cref="Id"/> is the photo id from the hornet (<c>{event ULID}-{frame}</c>), so re-uploads dedupe.
/// Paths are relative to the media root.
/// </summary>
public class MediaItem
{
    public required string Id { get; set; }
    public string? EventId { get; set; }
    public required string DeviceId { get; set; }
    public MediaKind Kind { get; set; }
    public DateTimeOffset Ts { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public required string Path { get; set; }
    public string? ThumbPath { get; set; }
    public long Bytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public required string Sha256 { get; set; }
    public bool Pinned { get; set; }

    /// <summary>home | city | sd (replayed from the hornet SD outbox) | replica (a copy sent from home to the city, spec 7.3).</summary>
    public string UploadedVia { get; set; } = "home";

    /// <summary>When home sent this photo to the city and the city kept it; null: not yet (or this node is the city).</summary>
    public DateTimeOffset? ReplicatedAt { get; set; }

    /// <summary>Failed sends so far, and when the next one may be tried (growing pauses, so one bad photo does not block the queue).</summary>
    public int ReplicaTries { get; set; }

    public DateTimeOffset? ReplicaNextAt { get; set; }

    /// <summary>A person asked for this photo to go to the city: it is sent even when it is past its queue time.</summary>
    public bool ReplicaForced { get; set; }

    /// <summary>How much earlier than the request a frame may be taken and still pass as fresh.</summary>
    public static readonly TimeSpan FreshSlack = TimeSpan.FromMinutes(1);

    /// <summary>The camera sent a frame taken well before it was asked for one (it could not take a fresh one).</summary>
    public bool TakenBefore(DateTimeOffset askedAt) => Ts < askedAt - FreshSlack;
}
