namespace Hive.Domain;

/// <summary>
/// A note in the owner's tree of notes: any depth via <see cref="ParentId"/>, order among siblings by <see cref="Sort"/>.
/// <see cref="Body"/> is Markdown. A deleted note (and its branch) stays in the trash until <see cref="DeletedAt"/> + 30 days.
/// </summary>
public class Note
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public int Sort { get; set; }
    public required string Title { get; set; }

    /// <summary>An emoji shown before the title (the same in the UI and in Telegram).</summary>
    public string? Icon { get; set; }

    /// <summary>Bookmark color: a key of the palette (red, orange, yellow, green, teal, blue, purple, gray).</summary>
    public string? Color { get; set; }

    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>The whole branch deleted together carries the same moment, so restoring brings back exactly that branch.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>A file attached to a note (pasted picture, imported file). The file lives under the media root, notes/{yyyy}/{MM}/.</summary>
public class NoteAttachment
{
    /// <summary>ULID.</summary>
    public required string Id { get; set; }
    public int NoteId { get; set; }
    public required string FileName { get; set; }
    public required string Mime { get; set; }
    public long Bytes { get; set; }
    public required string Sha256 { get; set; }

    /// <summary>Relative to the media root.</summary>
    public required string Path { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
