using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Media;
using Hive.Modules.Notes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Cluster;

/// <summary>
/// The city is the master of the notes (the dacha keeps nothing of its own once both are ready). The city says so in its readings
/// (<see cref="Cap"/>); the dacha, seeing it, moves its notes over once and from then on every request about notes, and the Telegram
/// assistant, goes to the city. A hive that has not been updated does not say it and does not listen: everything stays as it was.
/// </summary>
public static class NotesMasterPolicy
{
    public const string Cap = "notes-master";

    /// <summary>Kept on the dacha when its notes are in the city: from then on it asks the city for them.</summary>
    public const string MovedKey = "notes.master";

    /// <summary>The key both nodes can make from the secret they share; the dacha sends it, the city checks it.</summary>
    public static string Key(string secret) => CityBrokerLogin.Password(secret, "replica-notes");

    public static bool KeyMatches(string? sent, string secret) =>
        !string.IsNullOrEmpty(sent) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(Key(secret)));

    /// <summary>The strongest role the person has: the city trusts the dacha about who it is, never about more than the person is.</summary>
    public static string StrongestRole(ClaimsPrincipal user) =>
        user.IsInRole(HiveRoles.Admin) ? HiveRoles.Admin : user.IsInRole(HiveRoles.Member) ? HiveRoles.Member : HiveRoles.Viewer;

    /// <summary>The person the dacha vouches for, as a user of the city for this one request.</summary>
    public static ClaimsPrincipal Principal(string name, string role) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, HiveRoles.All.Contains(role) ? role : HiveRoles.Viewer)],
            "replica"));
}

/// <summary>Whether the dacha's notes are in the city. Remembered on disk: a restart must not send a note to the wrong place.</summary>
public sealed class NotesMasterState
{
    private readonly IOptions<ClusterOptions> options;
    private readonly IOptions<HiveOptions> hive;
    private readonly Lazy<bool> _moved;

    private volatile bool _movedNow;

    public NotesMasterState(IOptions<ClusterOptions> options, IOptions<HiveOptions> hive, IServiceScopeFactory scopes)
        : this(options, hive, () => Load(scopes))
    {
    }

    internal NotesMasterState(IOptions<ClusterOptions> options, IOptions<HiveOptions> hive, Func<bool> moved)
    {
        this.options = options;
        this.hive = hive;
        _moved = new Lazy<bool>(moved);
    }

    private bool Home => options.Value.Enabled && options.Value.NotesInCity && hive.Value.Node == "home";

    /// <summary>true: the notes of this hive are the city's, ask it.</summary>
    public bool Active => Home && (_movedNow || _moved.Value);

    public bool Moved => _movedNow || _moved.Value;

    private static bool Load(IServiceScopeFactory scopes)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        return db.Modes.AsNoTracking().Any(m => m.Key == NotesMasterPolicy.MovedKey && m.Value == "city");
    }

    public async Task MarkMovedAsync(HiveDbContext db, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var row = await db.Modes.FirstOrDefaultAsync(m => m.Key == NotesMasterPolicy.MovedKey, ct);
        if (row is null)
            db.Modes.Add(new Mode { Key = NotesMasterPolicy.MovedKey, Value = "city", UpdatedAt = now, UpdatedBy = "notes-master" });
        else
        {
            row.Value = "city";
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        _movedNow = true;
    }
}

/// <summary>How the dacha talks to the city about notes: the city's address from Settings, the key from the shared secret, one person per request.</summary>
public sealed class NotesMasterClient
{
    private readonly IOptions<CityBrokerOptions> secret;
    private readonly IServiceScopeFactory scopes;
    private readonly TimeProvider time;
    private readonly CityPin? pin;
    private readonly HttpClient Http;

    private string? _url;
    private DateTimeOffset _urlAt = DateTimeOffset.MinValue;

    public NotesMasterClient(IOptions<CityBrokerOptions> secret, IServiceScopeFactory scopes, TimeProvider time, CityPin pin)
        : this(secret, scopes, time, null, pin)
    {
    }

    /// <summary>The handler is replaced in tests.</summary>
    internal NotesMasterClient(IOptions<CityBrokerOptions> secret, IServiceScopeFactory scopes, TimeProvider time, HttpMessageHandler? handler, CityPin? pin = null)
    {
        this.secret = secret;
        this.scopes = scopes;
        this.time = time;
        this.pin = pin;
        Http = new HttpClient(handler ?? CityHttp.Handler(TimeSpan.FromSeconds(5), pin ?? throw new ArgumentNullException(nameof(pin)))) { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>Tests set the address of the city here instead of reading it from the settings.</summary>
    internal void UseUrl(string url)
    {
        _url = url;
        _urlAt = DateTimeOffset.MaxValue;
    }

    public async Task<string?> UrlAsync(CancellationToken ct)
    {
        if (_urlAt == DateTimeOffset.MaxValue)
            return _url;
        var now = time.GetUtcNow();
        if (now - _urlAt < TimeSpan.FromSeconds(30))
            return _url;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        _url = PhotoReplicator.CityUrl(await db.Modes.AsNoTracking().Where(m => m.Key == PhotoReplicator.UrlKey).Select(m => m.Value).FirstOrDefaultAsync(ct));
        _urlAt = now;
        return _url;
    }

    /// <summary>A request to the city on behalf of a person; the caller sets the content and disposes it.</summary>
    public async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string pathAndQuery, string user, string role, CancellationToken ct)
    {
        var url = await UrlAsync(ct) ?? throw new NoteMasterUnavailableException("The address of the city hive is not set (Settings → Fallback broker)");
        var request = new HttpRequestMessage(method, url + pathAndQuery);
        request.Headers.Add("X-Replica-Key", NotesMasterPolicy.Key(secret.Value.BridgePassword ?? ""));
        request.Headers.Add("X-Replica-User", Uri.EscapeDataString(user));
        request.Headers.Add("X-Replica-Role", role);
        return request;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct, bool streaming = false)
    {
        try
        {
            if (pin is not null)
                await pin.EnsureFreshAsync(ct);
            return await Http.SendAsync(request, streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new NoteMasterUnavailableException(pin?.Describe().Seen is { } seen && ex.InnerException is System.Security.Authentication.AuthenticationException
                ? $"The certificate of the city is not the pinned one (it shows {seen})"
                : "The city does not answer: " + ex.Message, ex);
        }
    }
}

/// <summary>
/// Both ends of the notes' move: on the city the requests of the dacha are let in by the key (as the person the dacha names), on the dacha,
/// once the notes are in the city, the requests about notes are passed on to it. Placed after authentication, before authorization.
/// </summary>
public sealed class NotesMasterMiddleware(
    RequestDelegate next,
    IOptions<ClusterOptions> options,
    IOptions<HiveOptions> hive,
    IOptions<CityBrokerOptions> secret,
    NotesMasterState state,
    NotesMasterClient client)
{
    private static readonly string[] PassedBack = ["Content-Type", "Content-Disposition", "ETag", "Cache-Control", "X-Content-Type-Options", "Accept-Ranges", "Content-Range", "Location"];
    private static readonly string[] PassedOn = ["Range", "If-None-Match", "If-Range", "Accept"];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api/notes") || !options.Value.Enabled)
        {
            await next(context);
            return;
        }

        // The city: the dacha speaks for a person.
        if (hive.Value.Node == "city" && context.Request.Headers.TryGetValue("X-Replica-Key", out var key))
        {
            if (string.IsNullOrEmpty(secret.Value.BridgePassword) || !NotesMasterPolicy.KeyMatches(key.ToString(), secret.Value.BridgePassword))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var name = Uri.UnescapeDataString(context.Request.Headers["X-Replica-User"].ToString());
            context.User = NotesMasterPolicy.Principal(name.Length > 0 ? name : "dacha", context.Request.Headers["X-Replica-Role"].ToString());
            await next(context);
            return;
        }

        // The dacha, notes in the city: whoever is signed in here is passed on to the city as themselves.
        if (hive.Value.Node == "home" && state.Active)
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await PassOnAsync(context);
            return;
        }
        await next(context);
    }

    private async Task PassOnAsync(HttpContext context)
    {
        var request = context.Request;
        var ct = context.RequestAborted;
        try
        {
            using var forward = await client.RequestAsync(new HttpMethod(request.Method), request.Path + request.QueryString, context.User.Identity!.Name ?? "", NotesMasterPolicy.StrongestRole(context.User), ct);
            foreach (var name in PassedOn)
                if (request.Headers.TryGetValue(name, out var value))
                    forward.Headers.TryAddWithoutValidation(name, value.ToArray());
            if (request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
            {
                forward.Content = new StreamContent(request.Body);
                if (request.ContentType is not null)
                    forward.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
                if (request.ContentLength is { } length)
                    forward.Content.Headers.ContentLength = length;
            }
            using var response = await client.SendAsync(forward, ct, streaming: true);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var name in PassedBack)
                if (response.Headers.TryGetValues(name, out var v) || response.Content.Headers.TryGetValues(name, out v))
                    context.Response.Headers[name] = v.ToArray();
            await response.Content.CopyToAsync(context.Response.Body, ct);
        }
        catch (NoteMasterUnavailableException ex) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { title = "Заметки хранятся в городе, а связи с ним сейчас нет: " + ex.Message }, ct);
        }
    }
}

/// <summary>The dacha's Telegram assistant (and anything else that is not a browser) reaching the notes of the city.</summary>
public sealed class RemoteNoteMaster(NotesMasterState state, NotesMasterClient client) : INoteMaster
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Active => state.Active;

    private static string Person(string by) => by.StartsWith("user:", StringComparison.Ordinal) ? by[5..] : by;

    public async Task<IReadOnlyList<string>> OutlineAsync(int max, CancellationToken ct)
    {
        using var request = await client.RequestAsync(HttpMethod.Get, $"/api/notes/assistant/outline?max={max}", "assistant", HiveRoles.Viewer, ct);
        using var response = await client.SendAsync(request, ct);
        return await ReadAsync<List<string>>(response, ct) ?? [];
    }

    public async Task<NotePathMatch> MatchPathAsync(IReadOnlyList<string> path, CancellationToken ct)
    {
        using var request = await client.RequestAsync(HttpMethod.Post, "/api/notes/assistant/match", "assistant", HiveRoles.Member, ct);
        request.Content = JsonContent.Create(new NotePathRequest(path), options: Json);
        using var response = await client.SendAsync(request, ct);
        return await ReadAsync<NotePathMatch>(response, ct) ?? new NotePathMatch([], path);
    }

    public async Task<int?> EnsurePathAsync(IReadOnlyList<string> path, string by, CancellationToken ct)
    {
        using var request = await client.RequestAsync(HttpMethod.Post, "/api/notes/assistant/ensure", Person(by), HiveRoles.Member, ct);
        request.Content = JsonContent.Create(new NotePathRequest(path), options: Json);
        using var response = await client.SendAsync(request, ct);
        return (await ReadAsync<NoteEnsureDto>(response, ct))?.ParentId;
    }

    public async Task<(NoteChange Result, NoteDto? Note)> CreateAsync(CreateNoteRequest note, string by, CancellationToken ct)
    {
        using var request = await client.RequestAsync(HttpMethod.Post, "/api/notes", Person(by), HiveRoles.Member, ct);
        request.Content = JsonContent.Create(note, options: Json);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (NoteChange.NotFound, null);
        if (response.StatusCode is HttpStatusCode.BadRequest)
            return (NoteChange.BadRequest, null);
        return (NoteChange.Ok, await ReadAsync<NoteDto>(response, ct));
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        // A refusal of the key or a broken city is "unavailable", not "the answer is nothing": the assistant must not say it saved.
        if (!response.IsSuccessStatusCode)
            throw new NoteMasterUnavailableException($"The city answered {(int)response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }
}

/// <summary>
/// Dacha: when the city says it keeps the notes and the dacha's are not there yet, moves them (notes first, then each file under the
/// name it had, so the pictures in the text still open), and only then switches. A failed round is tried again; nothing is lost on the way.
/// </summary>
public sealed class NotesMigrator(
    IOptions<ClusterOptions> options,
    IOptions<HiveOptions> hive,
    NotesMasterState state,
    NotesMasterClient client,
    NodeStatsReceiver city,
    IServiceScopeFactory scopes,
    MediaStore store,
    TimeProvider time,
    ILogger<NotesMigrator> logger) : BackgroundService
{
    private const int Batch = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider" || !options.Value.Enabled || !options.Value.NotesInCity || hive.Value.Node != "home")
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.ReplicaEveryS)), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Moving the notes to the city failed, will try again");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One try; true when the notes are in the city (now or before).</summary>
    internal async Task<bool> TickAsync(CancellationToken ct)
    {
        if (state.Moved)
            return true;
        if (!city.NotesMasterDeclared || await client.UrlAsync(ct) is null)
            return false;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var notes = await db.Notes.AsNoTracking().Where(n => n.DeletedAt == null).OrderBy(n => n.Id).ToListAsync(ct);
        var depth = Depths(notes);
        var map = new Dictionary<int, int>();
        foreach (var batch in notes.OrderBy(n => depth[n.Id]).ThenBy(n => n.Id).Chunk(Batch))
        {
            var body = new NotesImportRequest(batch.Select(n => new ImportNote(n.Id, n.ParentId, n.Sort, n.Title, n.Icon, n.Color, n.Body, n.CreatedAt, n.CreatedBy, n.UpdatedAt, n.UpdatedBy)).ToList());
            using var request = await client.RequestAsync(HttpMethod.Post, "/api/replica/notes/import", "dacha", HiveRoles.Admin, ct);
            request.Content = JsonContent.Create(body, options: Json);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The city refused the notes: {Status}", (int)response.StatusCode);
                return false;
            }
            foreach (var (from, to) in (await response.Content.ReadFromJsonAsync<NotesImportResult>(Json, ct))?.Map ?? new Dictionary<int, int>())
                map[from] = to;
        }

        var live = notes.Select(n => n.Id).ToHashSet();
        var files = await db.NoteAttachments.AsNoTracking().Where(a => live.Contains(a.NoteId)).ToListAsync(ct);
        foreach (var file in files)
        {
            byte[] data;
            try
            {
                data = await File.ReadAllBytesAsync(store.FullPath(file.Path), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Attachment {Id} of a note has no file here, it is left behind", file.Id);
                continue;
            }
            var query = $"?note={file.NoteId}&name={Uri.EscapeDataString(file.FileName)}&mime={Uri.EscapeDataString(file.Mime)}&created={file.CreatedAt.ToUnixTimeMilliseconds()}";
            using var request = await client.RequestAsync(HttpMethod.Put, $"/api/replica/notes/attachments/{file.Id}{query}", "dacha", HiveRoles.Admin, ct);
            request.Content = new ByteArrayContent(data);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The city refused the attachment {Id}: {Status}", file.Id, (int)response.StatusCode);
                return false;
            }
        }

        await state.MarkMovedAsync(db, time, ct);
        logger.LogInformation("{Notes} notes and {Files} files are in the city now; the notes of this hive are the city's", notes.Count, files.Count);
        return true;
    }

    /// <summary>How deep each note is (roots 0), so that parents go before children.</summary>
    internal static Dictionary<int, int> Depths(IReadOnlyList<Note> notes)
    {
        var parent = notes.ToDictionary(n => n.Id, n => n.ParentId);
        var depth = new Dictionary<int, int>();
        int Of(int id, int guard = 0) =>
            depth.TryGetValue(id, out var d) ? d
            : depth[id] = parent[id] is { } p && parent.ContainsKey(p) && guard < 256 ? Of(p, guard + 1) + 1 : 0;
        foreach (var n in notes)
            Of(n.Id);
        return depth;
    }
}

public static class NotesMasterEndpoints
{
    /// <summary>City: the dacha's move of its notes. Checked by the key; nothing else here changes the house.</summary>
    public static IEndpointRouteBuilder MapNotesMasterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/replica/notes").NodeLocal().WithTags("cluster").ExcludeFromDescription().AllowAnonymous();

        group.MapPost("/import", async (NotesImportRequest request, HttpRequest http, NotesImportService import,
            IOptions<ClusterOptions> options, IOptions<HiveOptions> hive, IOptions<CityBrokerOptions> secret, CancellationToken ct) =>
        {
            if (!Allowed(http, options.Value, hive.Value, secret.Value))
                return Results.Unauthorized();
            return Results.Ok(await import.ImportAsync(request, ct));
        });

        group.MapPut("/attachments/{id}", async (string id, int note, string? name, string? mime, long created, HttpRequest http, NotesImportService import,
            IOptions<ClusterOptions> options, IOptions<HiveOptions> hive, IOptions<CityBrokerOptions> secret, CancellationToken ct) =>
        {
            if (!Allowed(http, options.Value, hive.Value, secret.Value))
                return Results.Unauthorized();
            if (id.Length != 26 || !id.All(char.IsAsciiLetterOrDigit))
                return Results.BadRequest("not an attachment id");
            return await import.ImportAttachmentAsync(id, note, name, mime, DateTimeOffset.FromUnixTimeMilliseconds(created), http.Body, ct) switch
            {
                AttachmentChange.Ok => Results.Ok(),
                AttachmentChange.NotFound => Results.NotFound(),
                AttachmentChange.TooLarge => Results.StatusCode(StatusCodes.Status413PayloadTooLarge),
                _ => Results.BadRequest("an empty file"),
            };
        }).WithMetadata(new RequestSizeLimitAttribute(NoteAttachmentService.MaxBytes + 64 * 1024)).DisableAntiforgery();
        return app;
    }

    private static bool Allowed(HttpRequest http, ClusterOptions cluster, HiveOptions hive, CityBrokerOptions secret) =>
        cluster.Enabled && hive.Node == "city" && !string.IsNullOrEmpty(secret.BridgePassword)
        && NotesMasterPolicy.KeyMatches(http.Headers["X-Replica-Key"].ToString(), secret.BridgePassword);
}
