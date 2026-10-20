using System.Security.Claims;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;

namespace Hive.Modules.Notes;

/// <summary>Notes: a tree of Markdown pages with pictures and files. Everyone signed in reads them; admins and members edit.</summary>
public static class NotesModule
{
    public const string Name = "notes";

    public static IServiceCollection AddNotesModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<NoteService>();
        services.AddScoped<NoteAttachmentService>();
        services.AddHostedService<NotesTrashPurge>();
        return services;
    }

    private static string Actor(ClaimsPrincipal user) => $"user:{user.Identity?.Name}";

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private const string NoteRules = "A title of 1..200 characters, an icon of up to 16 characters, a palette color, a text of up to 1 000 000 characters";

    public static IEndpointRouteBuilder MapNotesEndpoints(this IEndpointRouteBuilder app)
    {
        var notes = app.MapGroup("/api/notes").WithTags("notes");

        notes.MapGet("", (NoteService n, CancellationToken ct) => n.TreeAsync(ct));
        notes.MapGet("/search", (string? q, NoteService n, CancellationToken ct) => n.SearchAsync(q, ct));
        notes.MapGet("/trash", (NoteService n, CancellationToken ct) => n.TrashAsync(ct));

        notes.MapGet("/{id:int}", async Task<Results<Ok<NoteDto>, NotFound>> (int id, NoteService n, CancellationToken ct) =>
            await n.GetAsync(id, ct) is { } note ? TypedResults.Ok(note) : TypedResults.NotFound());

        notes.MapPost("", async Task<Results<Created<NoteDto>, NotFound, ValidationProblem>> (
                CreateNoteRequest request, ClaimsPrincipal user, NoteService n, CancellationToken ct) =>
            {
                var (result, note) = await n.CreateAsync(request, Actor(user), ct);
                return result switch
                {
                    NoteChange.Ok => TypedResults.Created($"/api/notes/{note!.Id}", note),
                    NoteChange.NotFound => TypedResults.NotFound(),
                    _ => Invalid("title", NoteRules),
                };
            })
            .RequireAuthorization(HiveRoles.CanControl);

        notes.MapPatch("/{id:int}", async Task<Results<NoContent, NotFound, ValidationProblem>> (
                int id, UpdateNoteRequest request, ClaimsPrincipal user, NoteService n, CancellationToken ct) =>
                await n.UpdateAsync(id, request, Actor(user), ct) switch
                {
                    NoteChange.Ok => TypedResults.NoContent(),
                    NoteChange.NotFound => TypedResults.NotFound(),
                    _ => Invalid("title", NoteRules),
                })
            .RequireAuthorization(HiveRoles.CanControl);

        notes.MapPost("/{id:int}/move", async Task<Results<NoContent, NotFound, ValidationProblem>> (
                int id, MoveNoteRequest request, ClaimsPrincipal user, NoteService n, CancellationToken ct) =>
                await n.MoveAsync(id, request, Actor(user), ct) switch
                {
                    NoteChange.Ok => TypedResults.NoContent(),
                    NoteChange.NotFound => TypedResults.NotFound(),
                    _ => Invalid("parentId", "A note cannot go inside its own branch"),
                })
            .RequireAuthorization(HiveRoles.CanControl);

        notes.MapDelete("/{id:int}", async Task<Results<NoContent, NotFound>> (int id, ClaimsPrincipal user, NoteService n, CancellationToken ct) =>
                await n.DeleteAsync(id, Actor(user), ct) == NoteChange.Ok ? TypedResults.NoContent() : TypedResults.NotFound())
            .RequireAuthorization(HiveRoles.CanControl);

        notes.MapPost("/{id:int}/restore", async Task<Results<NoContent, NotFound>> (int id, ClaimsPrincipal user, NoteService n, CancellationToken ct) =>
                await n.RestoreAsync(id, Actor(user), ct) == NoteChange.Ok ? TypedResults.NoContent() : TypedResults.NotFound())
            .RequireAuthorization(HiveRoles.CanControl);

        // The file is the raw request body (a pasted picture is a Blob); the name comes in the query.
        notes.MapPost("/{id:int}/attachments", async Task<Results<Created<NoteAttachmentDto>, NotFound, ValidationProblem, ProblemHttpResult>> (
                int id, string? name, HttpRequest request, NoteAttachmentService a, CancellationToken ct) =>
            {
                var (result, attachment) = await a.AddAsync(id, name, request.ContentType, request.Body, ct);
                return result switch
                {
                    AttachmentChange.Ok => TypedResults.Created(attachment!.Url, attachment),
                    AttachmentChange.NotFound => TypedResults.NotFound(),
                    AttachmentChange.TooLarge => TypedResults.Problem($"Files up to {NoteAttachmentService.MaxBytes / 1024 / 1024} MB", statusCode: StatusCodes.Status413PayloadTooLarge),
                    _ => Invalid("body", "An empty file"),
                };
            })
            // Any file type is welcome (no Accepts metadata, which would answer 415); the document still says the body is a file.
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/octet-stream"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } },
                    },
                };
                return Task.CompletedTask;
            })
            .WithMetadata(new RequestSizeLimitAttribute(NoteAttachmentService.MaxBytes + 64 * 1024))
            .DisableAntiforgery()
            .RequireAuthorization(HiveRoles.CanControl);

        notes.MapGet("/attachments/{id}", GetAttachment)
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status404NotFound);

        notes.MapDelete("/attachments/{id}", async Task<Results<NoContent, NotFound>> (string id, NoteAttachmentService a, CancellationToken ct) =>
                await a.DeleteAsync(id, ct) == AttachmentChange.Ok ? TypedResults.NoContent() : TypedResults.NotFound())
            .RequireAuthorization(HiveRoles.CanControl);

        return app;
    }

    private static async Task<IResult> GetAttachment(string id, NoteAttachmentService a, HttpResponse response, CancellationToken ct)
    {
        if (await a.OpenAsync(id, ct) is not var (attachment, file))
            return Results.NotFound();
        response.Headers.XContentTypeOptions = "nosniff";
        var image = NoteAttachmentService.IsImage(attachment.Mime);
        // Pictures are shown in the note; any other file is a download, so an uploaded page never runs on the hive's origin.
        return Results.File(file, image ? attachment.Mime : "application/octet-stream",
            fileDownloadName: image ? null : attachment.FileName,
            entityTag: new EntityTagHeaderValue($"\"{attachment.Sha256}\""), enableRangeProcessing: true);
    }
}

/// <summary>Once a day empties the trash of notes deleted more than 30 days ago, files included.</summary>
public sealed class NotesTrashPurge(IServiceScopeFactory scopes, TimeProvider time, ILogger<NotesTrashPurge> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The build runs the app to write the OpenAPI document; it must not need a database.
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24), time);
        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var files = await scope.ServiceProvider.GetRequiredService<NoteService>().PurgeAsync(stoppingToken);
                    scope.ServiceProvider.GetRequiredService<NoteAttachmentService>().RemoveFiles(files);
                    if (files.Count > 0)
                        logger.LogInformation("Notes trash: removed {Files} attachment files", files.Count);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Notes trash purge failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
