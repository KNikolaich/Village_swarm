using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Hive.Modules.Media;

/// <summary>Photo ingest, previews and the media API (spec 7, 6.5).</summary>
public static class MediaModule
{
    public const string Name = "media";

    public static IServiceCollection AddMediaModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MediaOptions>(configuration.GetSection(MediaOptions.Section));
        services.AddSingleton<MediaStore>();
        services.AddSingleton<ThumbnailQueue>();
        services.AddScoped<ThumbnailService>();
        services.AddScoped<PhotoIngestService>();
        services.AddScoped<MediaQueries>();
        services.AddHostedService<MediaProcessor>();
        return services;
    }

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        // Hornets authenticate with their upload token, not a user session.
        app.MapPost("/api/ingest/photo", IngestPhoto)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(3 * 1024 * 1024))
            .WithTags("ingest");

        var media = app.MapGroup("/api/media").WithTags("media");
        media.MapGet("", (string? device, DateOnly? date, string? eventId, string? cursor, int? limit, MediaQueries q, CancellationToken ct) =>
            q.ListAsync(device, date, eventId, cursor, limit, ct));
        media.MapGet("/days", (DateOnly from, DateOnly to, string? device, MediaQueries q, CancellationToken ct) =>
            q.DaysAsync(from, to, device, ct));
        media.MapGet("/{id}", GetOriginal).Produces(StatusCodes.Status200OK, contentType: "image/jpeg").Produces(StatusCodes.Status404NotFound);
        media.MapGet("/{id}/thumb", GetThumb).Produces(StatusCodes.Status200OK, contentType: "image/webp").Produces(StatusCodes.Status404NotFound);
        media.MapPost("/{id}/pin", async Task<Results<NoContent, NotFound>> (string id, MediaQueries q, CancellationToken ct) =>
                await q.SetPinnedAsync(id, true, ct) ? TypedResults.NoContent() : TypedResults.NotFound())
            .RequireAuthorization(HiveRoles.CanControl);
        media.MapDelete("/{id}/pin", async Task<Results<NoContent, NotFound>> (string id, MediaQueries q, CancellationToken ct) =>
                await q.SetPinnedAsync(id, false, ct) ? TypedResults.NoContent() : TypedResults.NotFound())
            .RequireAuthorization(HiveRoles.CanControl);
        return app;
    }

    private static async Task<IResult> IngestPhoto(HttpRequest request, PhotoIngestService ingest, CancellationToken ct)
    {
        var auth = request.Headers.Authorization.ToString();
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, ct);

        var result = await ingest.IngestAsync(new PhotoUploadRequest(
            auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null,
            request.Headers["X-Device-Id"],
            request.Headers["X-Photo-Id"],
            request.Headers["X-Event-Id"],
            request.Headers["X-Event-Type"],
            request.Headers["X-Ts"],
            request.ContentType,
            body.ToArray()), ct);

        return result.Status switch
        {
            PhotoIngestStatus.Created => Results.Created($"/api/media/{result.Media!.Id}", MediaDto.From(result.Media)),
            PhotoIngestStatus.Duplicate => Results.Ok(MediaDto.From(result.Media!)),
            PhotoIngestStatus.Unauthorized => Results.Unauthorized(),
            PhotoIngestStatus.TooLarge => Results.Problem(result.Error, statusCode: StatusCodes.Status413PayloadTooLarge),
            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest),
        };
    }

    // Spec 7.2: the api streams files itself with Range and ETag support.
    private static async Task<IResult> GetOriginal(string id, MediaQueries q, MediaStore store, CancellationToken ct)
    {
        var item = await q.FindAsync(id, ct);
        if (item is null || !File.Exists(store.FullPath(item.Path)))
            return Results.NotFound();
        return Results.File(store.FullPath(item.Path), "image/jpeg", enableRangeProcessing: true,
            entityTag: new EntityTagHeaderValue($"\"{item.Sha256}\""), fileDownloadName: null);
    }

    private static async Task<IResult> GetThumb(string id, ThumbnailService thumbs, MediaStore store, CancellationToken ct)
    {
        var path = await thumbs.EnsureAsync(id, ct);
        return path is null
            ? Results.NotFound()
            : Results.File(store.FullPath(path), "image/webp", entityTag: new EntityTagHeaderValue($"\"t-{id}\""));
    }
}
