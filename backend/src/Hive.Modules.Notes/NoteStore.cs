using Hive.Domain;

namespace Hive.Modules.Notes;

/// <summary>
/// What the Telegram assistant needs of the notes: where a note goes, and putting it there. Where the notes live is not its business:
/// on a hive that is the master it is this database, on the dacha with the city as the master it is the city's.
/// </summary>
public interface INoteStore
{
    Task<IReadOnlyList<string>> OutlineAsync(int max, CancellationToken ct);

    Task<NotePathMatch> MatchPathAsync(IReadOnlyList<string> path, CancellationToken ct);

    Task<int?> EnsurePathAsync(IReadOnlyList<string> path, string by, CancellationToken ct);

    Task<(NoteChange Result, NoteDto? Note)> CreateAsync(CreateNoteRequest request, string by, CancellationToken ct);
}

/// <summary>The notes live in another hive (the city): its answer in place of this database. Registered by the cluster module.</summary>
public interface INoteMaster : INoteStore
{
    /// <summary>true: the notes of this hive are kept by the other one, use this in place of the local database.</summary>
    bool Active { get; }
}

/// <summary>The master of the notes is out of reach: nothing was saved here, so that two copies never drift apart.</summary>
public sealed class NoteMasterUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Picks, per call, the local database or the master of the notes.</summary>
public sealed class NoteStoreRouter(NoteService local, IEnumerable<INoteMaster> masters) : INoteStore
{
    private INoteStore Store => masters.FirstOrDefault(m => m.Active) is { } master ? master : local;

    public Task<IReadOnlyList<string>> OutlineAsync(int max, CancellationToken ct) => Store.OutlineAsync(max, ct);

    public Task<NotePathMatch> MatchPathAsync(IReadOnlyList<string> path, CancellationToken ct) => Store.MatchPathAsync(path, ct);

    public Task<int?> EnsurePathAsync(IReadOnlyList<string> path, string by, CancellationToken ct) => Store.EnsurePathAsync(path, by, ct);

    public Task<(NoteChange Result, NoteDto? Note)> CreateAsync(CreateNoteRequest request, string by, CancellationToken ct) => Store.CreateAsync(request, by, ct);
}

/// <summary>A path of titles, as the assistant asks about it.</summary>
public sealed record NotePathRequest(IReadOnlyList<string> Path);

public sealed record NoteEnsureDto(int? ParentId);
