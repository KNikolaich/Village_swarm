using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Hive.Modules.Notes;

/// <summary>One row of the tree: everything the side panel draws, without the text.</summary>
public sealed record NoteNodeDto(int Id, int? ParentId, int Sort, string Title, string? Icon, string? Color, DateTimeOffset UpdatedAt);

public sealed record NoteCrumbDto(int Id, string Title, string? Icon);

public sealed record NoteDto(
    int Id, int? ParentId, int Sort, string Title, string? Icon, string? Color, string Body,
    DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset UpdatedAt, string? UpdatedBy,
    IReadOnlyList<NoteCrumbDto> Path, IReadOnlyList<NoteAttachmentDto> Attachments);

/// <summary>Index null puts the note after its siblings.</summary>
public sealed record CreateNoteRequest(string Title, int? ParentId = null, string? Icon = null, string? Color = null, string? Body = null, int? Index = null);

/// <summary>Null leaves a field as it is; an empty icon or color clears it.</summary>
public sealed record UpdateNoteRequest(string? Title = null, string? Icon = null, string? Color = null, string? Body = null);

/// <summary>ParentId null makes the note a root; Index is its place among the new siblings (clamped).</summary>
public sealed record MoveNoteRequest(int? ParentId, int Index);

public sealed record NoteSearchHitDto(int Id, string Title, string? Icon, string? Color, string Snippet, IReadOnlyList<NoteCrumbDto> Path);

/// <summary>A branch in the trash: the note that was deleted, how many notes went with it, and when it goes for good.</summary>
public sealed record TrashedNoteDto(int Id, string Title, string? Icon, string? Color, int Notes, DateTimeOffset DeletedAt, DateTimeOffset PurgeAt);

public enum NoteChange { Ok, NotFound, BadRequest }

/// <summary>The owner's tree of notes: Markdown text, an emoji and a bookmark color in the header, any depth, drag and drop between branches.</summary>
public sealed class NoteService(HiveDbContext db, TimeProvider time)
{
    public const int MaxTitle = 200;
    public const int MaxBody = 1_000_000;
    public const int MaxIcon = 16;
    public static readonly TimeSpan TrashKeep = TimeSpan.FromDays(30);

    public static readonly IReadOnlySet<string> Colors =
        new HashSet<string>(StringComparer.Ordinal) { "red", "orange", "yellow", "green", "teal", "blue", "purple", "gray" };

    private IQueryable<Note> Live => db.Notes.Where(n => n.DeletedAt == null);

    public async Task<IReadOnlyList<NoteNodeDto>> TreeAsync(CancellationToken ct) =>
        await Live.AsNoTracking().OrderBy(n => n.ParentId).ThenBy(n => n.Sort).ThenBy(n => n.Id)
            .Select(n => new NoteNodeDto(n.Id, n.ParentId, n.Sort, n.Title, n.Icon, n.Color, n.UpdatedAt))
            .ToListAsync(ct);

    public async Task<NoteDto?> GetAsync(int id, CancellationToken ct)
    {
        var note = await Live.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null)
            return null;
        var attachments = await db.NoteAttachments.AsNoTracking().Where(a => a.NoteId == id).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        return new NoteDto(note.Id, note.ParentId, note.Sort, note.Title, note.Icon, note.Color, note.Body,
            note.CreatedAt, note.CreatedBy, note.UpdatedAt, note.UpdatedBy,
            await PathAsync(note.ParentId, ct), attachments.Select(NoteAttachmentDto.From).ToList());
    }

    public async Task<(NoteChange Result, NoteDto? Note)> CreateAsync(CreateNoteRequest request, string by, CancellationToken ct)
    {
        var title = request.Title?.Trim() ?? "";
        if (!TitleOk(title) || !IconOk(request.Icon) || !ColorOk(request.Color) || (request.Body?.Length ?? 0) > MaxBody)
            return (NoteChange.BadRequest, null);
        if (request.ParentId is { } parent && !await Live.AnyAsync(n => n.Id == parent, ct))
            return (NoteChange.NotFound, null);

        var now = time.GetUtcNow();
        var note = new Note
        {
            ParentId = request.ParentId,
            Title = title,
            Icon = Blank(request.Icon),
            Color = Blank(request.Color),
            Body = request.Body ?? "",
            CreatedAt = now,
            CreatedBy = by,
            UpdatedAt = now,
            UpdatedBy = by,
        };
        var siblings = await SiblingsAsync(request.ParentId, null, ct);
        siblings.Insert(Math.Clamp(request.Index ?? siblings.Count, 0, siblings.Count), note);
        db.Notes.Add(note);
        Renumber(siblings);
        await db.SaveChangesAsync(ct);
        return (NoteChange.Ok, await GetAsync(note.Id, ct));
    }

    public async Task<NoteChange> UpdateAsync(int id, UpdateNoteRequest request, string by, CancellationToken ct)
    {
        var title = request.Title?.Trim();
        if ((title is not null && !TitleOk(title)) || !IconOk(request.Icon) || !ColorOk(request.Color) || (request.Body?.Length ?? 0) > MaxBody)
            return NoteChange.BadRequest;
        var note = await Live.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null)
            return NoteChange.NotFound;
        if (title is not null)
            note.Title = title;
        if (request.Icon is not null)
            note.Icon = Blank(request.Icon);
        if (request.Color is not null)
            note.Color = Blank(request.Color);
        if (request.Body is not null)
            note.Body = request.Body;
        note.UpdatedAt = time.GetUtcNow();
        note.UpdatedBy = by;
        await db.SaveChangesAsync(ct);
        return NoteChange.Ok;
    }

    /// <summary>Moves a note with its branch under another note or to the root. A note cannot go inside its own branch.</summary>
    public async Task<NoteChange> MoveAsync(int id, MoveNoteRequest request, string by, CancellationToken ct)
    {
        var note = await Live.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null)
            return NoteChange.NotFound;
        if (request.ParentId is { } parent)
        {
            if (!await Live.AnyAsync(n => n.Id == parent, ct))
                return NoteChange.NotFound;
            if (parent == id || (await AncestorsAsync(parent, ct)).Contains(id))
                return NoteChange.BadRequest;
        }

        if (note.ParentId != request.ParentId)
        {
            Renumber(await SiblingsAsync(note.ParentId, id, ct));
            note.ParentId = request.ParentId;
        }
        var siblings = await SiblingsAsync(request.ParentId, id, ct);
        siblings.Insert(Math.Clamp(request.Index, 0, siblings.Count), note);
        Renumber(siblings);
        Audit.Record(db, time.GetUtcNow(), by, "note_move", id.ToString(System.Globalization.CultureInfo.InvariantCulture), new { request.ParentId, request.Index });
        await db.SaveChangesAsync(ct);
        return NoteChange.Ok;
    }

    /// <summary>Puts the note and its whole branch in the trash.</summary>
    public async Task<NoteChange> DeleteAsync(int id, string by, CancellationToken ct)
    {
        var note = await Live.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null)
            return NoteChange.NotFound;
        var branch = await BranchAsync(id, live: true, ct);
        var now = time.GetUtcNow();
        foreach (var n in await db.Notes.Where(n => branch.Contains(n.Id)).ToListAsync(ct))
            n.DeletedAt = now;
        Renumber(await SiblingsAsync(note.ParentId, id, ct));
        Audit.Record(db, now, by, "note_delete", note.Title, new { id, notes = branch.Count });
        await db.SaveChangesAsync(ct);
        return NoteChange.Ok;
    }

    /// <summary>Brings back a branch from the trash; if its parent is gone (deleted or purged), the branch becomes a root.</summary>
    public async Task<NoteChange> RestoreAsync(int id, string by, CancellationToken ct)
    {
        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id && n.DeletedAt != null, ct);
        if (note is null)
            return NoteChange.NotFound;
        var deletedAt = note.DeletedAt;
        var branch = await BranchAsync(id, live: false, ct);
        foreach (var n in await db.Notes.Where(n => branch.Contains(n.Id) && n.DeletedAt == deletedAt).ToListAsync(ct))
            n.DeletedAt = null;
        if (note.ParentId is { } parent && !await Live.AnyAsync(n => n.Id == parent, ct))
            note.ParentId = null;
        var siblings = await SiblingsAsync(note.ParentId, id, ct);
        siblings.Add(note);
        Renumber(siblings);
        Audit.Record(db, time.GetUtcNow(), by, "note_restore", note.Title, new { id });
        await db.SaveChangesAsync(ct);
        return NoteChange.Ok;
    }

    /// <summary>The branches that were deleted (not the notes that went with them), newest first.</summary>
    public async Task<IReadOnlyList<TrashedNoteDto>> TrashAsync(CancellationToken ct)
    {
        var deleted = await db.Notes.AsNoTracking().Where(n => n.DeletedAt != null)
            .Select(n => new { n.Id, n.ParentId, n.Title, n.Icon, n.Color, DeletedAt = n.DeletedAt!.Value }).ToListAsync(ct);
        var byId = deleted.ToDictionary(n => n.Id);
        // A branch's top is a deleted note whose parent was not deleted in the same go.
        var tops = deleted.Where(n => n.ParentId is not { } p || !byId.TryGetValue(p, out var parent) || parent.DeletedAt != n.DeletedAt).ToList();
        int Count(int topId, DateTimeOffset at)
        {
            var count = 1;
            foreach (var child in deleted.Where(c => c.ParentId == topId && c.DeletedAt == at))
                count += Count(child.Id, at);
            return count;
        }
        return tops.OrderByDescending(n => n.DeletedAt)
            .Select(n => new TrashedNoteDto(n.Id, n.Title, n.Icon, n.Color, Count(n.Id, n.DeletedAt), n.DeletedAt, n.DeletedAt + TrashKeep))
            .ToList();
    }

    /// <summary>Title or text contains the words (Russian word forms match too), newest first.</summary>
    public async Task<IReadOnlyList<NoteSearchHitDto>> SearchAsync(string? q, CancellationToken ct)
    {
        q = q?.Trim();
        if (string.IsNullOrEmpty(q))
            return [];
        var like = $"%{q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
        var hits = await Live.AsNoTracking()
            .Where(n => EF.Functions.ILike(n.Title, like) || EF.Functions.ILike(n.Body, like)
                || EF.Functions.ToTsVector("russian", n.Title + " " + n.Body).Matches(EF.Functions.PlainToTsQuery("russian", q)))
            .OrderByDescending(n => n.UpdatedAt).Take(50)
            .Select(n => new { n.Id, n.ParentId, n.Title, n.Icon, n.Color, n.Body })
            .ToListAsync(ct);
        var result = new List<NoteSearchHitDto>(hits.Count);
        foreach (var h in hits)
            result.Add(new NoteSearchHitDto(h.Id, h.Title, h.Icon, h.Color, Snippet(h.Body, q), await PathAsync(h.ParentId, ct)));
        return result;
    }

    /// <summary>Notes deleted longer than <see cref="TrashKeep"/> ago go for good; returns their attachment files to remove.</summary>
    public async Task<IReadOnlyList<string>> PurgeAsync(CancellationToken ct)
    {
        var cut = time.GetUtcNow() - TrashKeep;
        var old = await db.Notes.Where(n => n.DeletedAt != null && n.DeletedAt < cut).Select(n => n.Id).ToListAsync(ct);
        if (old.Count == 0)
            return [];
        var files = await db.NoteAttachments.Where(a => old.Contains(a.NoteId)).Select(a => a.Path).ToListAsync(ct);
        await db.Notes.Where(n => old.Contains(n.Id)).ExecuteDeleteAsync(ct);
        return files;
    }

    internal static string Snippet(string body, string q)
    {
        var flat = body.ReplaceLineEndings(" ");
        var at = flat.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, at < 0 ? 0 : at - 60);
        var piece = flat.Substring(start, Math.Min(160, flat.Length - start)).Trim();
        return (start > 0 ? "…" : "") + piece + (start + 160 < flat.Length ? "…" : "");
    }

    /// <summary>Titles from the root down to <paramref name="parentId"/> (inclusive).</summary>
    public async Task<IReadOnlyList<NoteCrumbDto>> PathAsync(int? parentId, CancellationToken ct)
    {
        var path = new List<NoteCrumbDto>();
        var seen = new HashSet<int>();
        while (parentId is { } id && seen.Add(id))
        {
            var n = await db.Notes.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Id, x.ParentId, x.Title, x.Icon }).FirstOrDefaultAsync(ct);
            if (n is null)
                break;
            path.Insert(0, new NoteCrumbDto(n.Id, n.Title, n.Icon));
            parentId = n.ParentId;
        }
        return path;
    }

    private async Task<HashSet<int>> AncestorsAsync(int id, CancellationToken ct) =>
        (await PathAsync(id, ct)).Select(c => c.Id).ToHashSet();

    /// <summary>The note and everything under it.</summary>
    private async Task<List<int>> BranchAsync(int id, bool live, CancellationToken ct)
    {
        var links = await db.Notes.AsNoTracking().Where(n => live ? n.DeletedAt == null : n.DeletedAt != null)
            .Select(n => new { n.Id, n.ParentId }).ToListAsync(ct);
        var children = links.Where(l => l.ParentId != null).ToLookup(l => l.ParentId!.Value, l => l.Id);
        var branch = new List<int> { id };
        for (var i = 0; i < branch.Count; i++)
            branch.AddRange(children[branch[i]]);
        return branch;
    }

    private async Task<List<Note>> SiblingsAsync(int? parentId, int? except, CancellationToken ct) =>
        await Live.Where(n => n.ParentId == parentId && n.Id != except).OrderBy(n => n.Sort).ThenBy(n => n.Id).ToListAsync(ct);

    private static void Renumber(List<Note> siblings)
    {
        for (var i = 0; i < siblings.Count; i++)
            siblings[i].Sort = i;
    }

    private static bool TitleOk(string title) => title.Length is > 0 and <= MaxTitle;

    private static bool IconOk(string? icon) => icon is null || icon.Trim().Length <= MaxIcon;

    private static bool ColorOk(string? color) => string.IsNullOrEmpty(color) || Colors.Contains(color);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
