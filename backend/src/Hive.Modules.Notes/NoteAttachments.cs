using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Hive.Contracts;
using Hive.Domain;
using Hive.Infrastructure.Data;
using Hive.Modules.Media;
using Microsoft.EntityFrameworkCore;

namespace Hive.Modules.Notes;

/// <summary>Markdown is what the editor inserts: a picture for images, a link for other files.</summary>
public sealed record NoteAttachmentDto(string Id, string FileName, string Mime, long Bytes, bool IsImage, string Url, string Markdown, DateTimeOffset CreatedAt)
{
    public static NoteAttachmentDto From(NoteAttachment a)
    {
        var url = $"/api/notes/attachments/{a.Id}";
        var image = NoteAttachmentService.IsImage(a.Mime);
        var label = a.FileName.Replace("[", "(").Replace("]", ")");
        return new(a.Id, a.FileName, a.Mime, a.Bytes, image, url, image ? $"![{label}]({url})" : $"[{label}]({url})", a.CreatedAt);
    }
}

public enum AttachmentChange { Ok, NotFound, BadRequest, TooLarge }

/// <summary>Files pasted or imported into notes. They live in the media volume (notes/{yyyy}/{MM}/), so the nightly backup takes them too.</summary>
public sealed partial class NoteAttachmentService(HiveDbContext db, MediaStore store, TimeProvider time)
{
    public const long MaxBytes = 10 * 1024 * 1024;

    /// <summary>Shown in the page itself. Anything else (SVG and HTML included) is only offered for download.</summary>
    private static readonly IReadOnlySet<string> InlineImages =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif", "image/webp" };

    public static bool IsImage(string mime) => InlineImages.Contains(mime);

    public async Task<(AttachmentChange Result, NoteAttachmentDto? Attachment)> AddAsync(int noteId, string? fileName, string? contentType, Stream body, CancellationToken ct)
    {
        if (!await db.Notes.AnyAsync(n => n.Id == noteId && n.DeletedAt == null, ct))
            return (AttachmentChange.NotFound, null);

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                return (AttachmentChange.TooLarge, null);
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0)
            return (AttachmentChange.BadRequest, null);

        var data = buffer.ToArray();
        var name = CleanName(fileName);
        var mime = Sniff(data) ?? CleanMime(contentType);
        var now = time.GetUtcNow();
        var id = Ulid.New(now);
        var path = string.Join('/', "notes", now.ToString("yyyy/MM", CultureInfo.InvariantCulture), id + Extension(name, mime));
        await store.WriteAtomicAsync(path, data, ct);

        var attachment = new NoteAttachment
        {
            Id = id,
            NoteId = noteId,
            FileName = name,
            Mime = mime,
            Bytes = data.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)),
            Path = path,
            CreatedAt = now,
        };
        db.NoteAttachments.Add(attachment);
        await db.SaveChangesAsync(ct);
        return (AttachmentChange.Ok, NoteAttachmentDto.From(attachment));
    }

    /// <summary>The attachment and its file on disk, or null. Attachments of notes in the trash still open (the note can come back).</summary>
    public async Task<(NoteAttachment Attachment, string File)?> OpenAsync(string id, CancellationToken ct)
    {
        var a = await db.NoteAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null)
            return null;
        var file = store.FullPath(a.Path);
        return File.Exists(file) ? (a, file) : null;
    }

    public async Task<AttachmentChange> DeleteAsync(string id, CancellationToken ct)
    {
        var a = await db.NoteAttachments.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null)
            return AttachmentChange.NotFound;
        db.NoteAttachments.Remove(a);
        await db.SaveChangesAsync(ct);
        RemoveFiles([a.Path]);
        return AttachmentChange.Ok;
    }

    public void RemoveFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var file = store.FullPath(path);
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    /// <summary>Pictures are recognised by their first bytes, not by what the browser claims.</summary>
    internal static string? Sniff(ReadOnlySpan<byte> d) => d switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        [0x25, 0x50, 0x44, 0x46, ..] => "application/pdf",
        _ => null,
    };

    internal static string CleanName(string? name)
    {
        name = Path.GetFileName((name ?? "").Replace('\\', '/')).Trim();
        name = ControlChars().Replace(name, "");
        if (name.Length == 0)
            name = "file";
        return name.Length > 255 ? name[..255] : name;
    }

    private static string CleanMime(string? contentType)
    {
        var mime = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        // A browser saying image/png for something that is not a PNG gets a plain download.
        return mime.Length is > 0 and <= 128 && MimeShape().IsMatch(mime) && !InlineImages.Contains(mime) ? mime : "application/octet-stream";
    }

    private static string Extension(string name, string mime)
    {
        var known = mime switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "application/pdf" => ".pdf",
            _ => null,
        };
        if (known is not null)
            return known;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return SafeExtension().IsMatch(ext) ? ext : ".bin";
    }

    [GeneratedRegex(@"[\p{Cc}""]")]
    private static partial Regex ControlChars();

    [GeneratedRegex(@"^[a-z0-9.+-]+/[a-z0-9.+-]+$")]
    private static partial Regex MimeShape();

    [GeneratedRegex(@"^\.[a-z0-9]{1,8}$")]
    private static partial Regex SafeExtension();
}
