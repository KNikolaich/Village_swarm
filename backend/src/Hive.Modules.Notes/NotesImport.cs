using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Modules.Media;
using Microsoft.EntityFrameworkCore;

namespace Hive.Modules.Notes;

/// <summary>A note as the dacha had it before the city became the master of the notes.</summary>
public sealed record ImportNote(
    int Id, int? ParentId, int Sort, string Title, string? Icon, string? Color, string Body,
    DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset UpdatedAt, string? UpdatedBy);

public sealed record NotesImportRequest(IReadOnlyList<ImportNote> Notes);

/// <summary>For each note the dacha sent: the number it has here. A note sent twice is one note.</summary>
public sealed record NotesImportResult(IReadOnlyDictionary<int, int> Map);

/// <summary>
/// The city takes the notes the dacha kept until now (spec: the city is the master of the notes). Idempotent: what was taken before is
/// remembered (the dacha's number → this one's), so a repeated or interrupted move does not make copies.
/// </summary>
public sealed class NotesImportService(HiveDbContext db, MediaStore store, TimeProvider time)
{
    public const string MapKey = "notes.import-map";

    public async Task<Dictionary<int, int>> LoadMapAsync(CancellationToken ct)
    {
        var json = await db.Modes.AsNoTracking().Where(m => m.Key == MapKey).Select(m => m.Value).FirstOrDefaultAsync(ct);
        return json is null ? [] : JsonSerializer.Deserialize<Dictionary<int, int>>(json) ?? [];
    }

    public async Task<NotesImportResult> ImportAsync(NotesImportRequest request, CancellationToken ct)
    {
        var map = await LoadMapAsync(ct);
        var byId = request.Notes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        // Parents before children: the ones whose parent is already here (or is a root) go first, in rounds.
        var pending = byId.Values.Where(n => !map.ContainsKey(n.Id)).ToList();
        var round = 0;
        while (pending.Count > 0 && round++ < 64)
        {
            var ready = pending.Where(n => n.ParentId is null || map.ContainsKey(n.ParentId.Value) || !byId.ContainsKey(n.ParentId.Value)).ToList();
            if (ready.Count == 0)
                break;
            var added = new List<(ImportNote Source, Note Note)>();
            foreach (var source in ready.OrderBy(n => n.Sort).ThenBy(n => n.Id))
            {
                int? parent = source.ParentId is { } p && map.TryGetValue(p, out var mapped) ? mapped : null;
                var sort = await db.Notes.Where(n => n.ParentId == parent).Select(n => (int?)n.Sort).MaxAsync(ct) + 1 ?? 0;
                var note = new Note
                {
                    ParentId = parent,
                    Sort = sort,
                    Title = Clip(source.Title.Trim(), NoteService.MaxTitle) is { Length: > 0 } title ? title : "—",
                    Icon = Clip(source.Icon, NoteService.MaxIcon),
                    Color = source.Color is { } c && NoteService.Colors.Contains(c) ? c : null,
                    Body = Clip(source.Body, NoteService.MaxBody) ?? "",
                    CreatedAt = source.CreatedAt,
                    CreatedBy = source.CreatedBy,
                    UpdatedAt = source.UpdatedAt,
                    UpdatedBy = source.UpdatedBy,
                };
                db.Notes.Add(note);
                added.Add((source, note));
                // Siblings of one parent get their numbers one after another; children wait for the next round.
                await db.SaveChangesAsync(ct);
            }
            foreach (var (source, note) in added)
                map[source.Id] = note.Id;
            pending.RemoveAll(n => map.ContainsKey(n.Id));
        }
        await SaveMapAsync(map, ct);
        return new NotesImportResult(map);
    }

    private static string? Clip(string? text, int max) => text is null ? null : text.Length > max ? text[..max] : text;

    private async Task SaveMapAsync(Dictionary<int, int> map, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var json = JsonSerializer.Serialize(map);
        var row = await db.Modes.FirstOrDefaultAsync(m => m.Key == MapKey, ct);
        if (row is null)
            db.Modes.Add(new Mode { Key = MapKey, Value = json, UpdatedAt = now, UpdatedBy = "notes-import" });
        else
        {
            row.Value = json;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>One file of an imported note, under the name (ULID) it had, so that the pictures in the text still find it.</summary>
    public async Task<AttachmentChange> ImportAttachmentAsync(
        string id, int homeNoteId, string? fileName, string? mime, DateTimeOffset createdAt, Stream body, CancellationToken ct)
    {
        if (await db.NoteAttachments.AnyAsync(a => a.Id == id, ct))
            return AttachmentChange.Ok;
        var map = await LoadMapAsync(ct);
        if (!map.TryGetValue(homeNoteId, out var noteId) || !await db.Notes.AnyAsync(n => n.Id == noteId, ct))
            return AttachmentChange.NotFound;

        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, ct);
        if (buffer.Length == 0 || buffer.Length > NoteAttachmentService.MaxBytes)
            return buffer.Length == 0 ? AttachmentChange.BadRequest : AttachmentChange.TooLarge;
        var data = buffer.ToArray();
        var name = NoteAttachmentService.CleanName(fileName);
        var kind = NoteAttachmentService.Sniff(data) ?? (string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime);
        var path = string.Join('/', "notes", createdAt.ToString("yyyy/MM", CultureInfo.InvariantCulture), id + NoteAttachmentService.ExtensionFor(name, kind));
        await store.WriteAtomicAsync(path, data, ct);
        db.NoteAttachments.Add(new NoteAttachment
        {
            Id = id, NoteId = noteId, FileName = name, Mime = kind, Bytes = data.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)), Path = path, CreatedAt = createdAt,
        });
        await db.SaveChangesAsync(ct);
        return AttachmentChange.Ok;
    }
}
