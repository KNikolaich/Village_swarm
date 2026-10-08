using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Hive.Contracts;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Media;

public enum PhotoIngestStatus
{
    Created,
    Duplicate,
    Unauthorized,
    Invalid,
    TooLarge,
}

public sealed record PhotoIngestResult(PhotoIngestStatus Status, string? Error = null, MediaItem? Media = null);

/// <summary>Request data of POST /api/ingest/photo (headers per spec 5.4).</summary>
public sealed record PhotoUploadRequest(
    string? BearerToken, string? DeviceIdHeader, string? PhotoId, string? EventId, string? EventType, string? Ts, string? ContentType, byte[] Body);

/// <summary>Validates, stores and registers a photo uploaded by a hornet (spec 7.2).</summary>
public sealed partial class PhotoIngestService(
    HiveDbContext db,
    MediaStore store,
    ThumbnailQueue thumbnails,
    IOptions<MediaOptions> mediaOptions,
    IOptions<HiveOptions> hiveOptions,
    TimeProvider time)
{
    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{26}-[0-9]{1,2}$")]
    private static partial Regex PhotoIdRegex();

    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{26}$")]
    private static partial Regex UlidRegex();

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    public static bool IsJpeg(ReadOnlySpan<byte> data) =>
        data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public async Task<PhotoIngestResult> IngestAsync(PhotoUploadRequest request, CancellationToken ct)
    {
        var deviceId = await AuthenticateAsync(request, ct);
        if (deviceId is null)
            return new(PhotoIngestStatus.Unauthorized, "invalid upload token");

        if (request.Body.LongLength > mediaOptions.Value.MaxPhotoBytes)
            return new(PhotoIngestStatus.TooLarge, $"photo exceeds {mediaOptions.Value.MaxPhotoBytes} bytes");
        if (request.PhotoId is null || !PhotoIdRegex().IsMatch(request.PhotoId))
            return new(PhotoIngestStatus.Invalid, "X-Photo-Id must be <event ULID>-<frame>");
        if (request.EventId is not null && !UlidRegex().IsMatch(request.EventId))
            return new(PhotoIngestStatus.Invalid, "X-Event-Id must be a ULID");
        if (request.ContentType?.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase) != true || !IsJpeg(request.Body))
            return new(PhotoIngestStatus.Invalid, "body must be image/jpeg");

        var existing = await db.Media.AsNoTracking().FirstOrDefaultAsync(m => m.Id == request.PhotoId, ct);
        if (existing is not null)
            return new(PhotoIngestStatus.Duplicate, Media: existing);

        if (ImageTools.Identify(request.Body) is not { } size)
            return new(PhotoIngestStatus.Invalid, "corrupt JPEG");

        var receivedAt = time.GetUtcNow();
        var ts = long.TryParse(request.Ts, out var ms) ? MessageTime.Effective(ms, receivedAt) : receivedAt;
        var eventType = request.EventId is null
            ? "snapshot"
            : Topics.IsValidSegment(request.EventType) ? request.EventType!
            : await db.Events.Where(e => e.Id == request.EventId).Select(e => e.Type).FirstOrDefaultAsync(ct) ?? "event";
        var local = hiveOptions.Value.ToLocal(ts);

        var media = new MediaItem
        {
            Id = request.PhotoId,
            EventId = request.EventId,
            DeviceId = deviceId,
            Kind = request.EventId is null || eventType == "snapshot" ? MediaKind.Snapshot : MediaKind.Photo,
            Ts = ts,
            ReceivedAt = receivedAt,
            Path = MediaStore.PhotoPath(deviceId, local, eventType, request.PhotoId),
            Bytes = request.Body.LongLength,
            Width = size.Width,
            Height = size.Height,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(request.Body)),
            UploadedVia = ms > 0 && receivedAt - ts > TimeSpan.FromMinutes(5) ? "sd" : "home",
        };

        await store.WriteAtomicAsync(media.Path, request.Body, ct);
        db.Media.Add(media);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return new(PhotoIngestStatus.Duplicate, Media: media); // a concurrent retry of the same photo won
        }

        thumbnails.Enqueue(media.Id);
        return new(PhotoIngestStatus.Created, Media: media);
    }

    private async Task<string?> AuthenticateAsync(PhotoUploadRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.BearerToken))
        {
            var hash = HashToken(request.BearerToken);
            return await db.Devices.Where(d => d.UploadTokenHash == hash && d.Status != DeviceStatus.Disabled)
                .Select(d => d.DeviceId).FirstOrDefaultAsync(ct);
        }
        return mediaOptions.Value.AllowUploadsWithoutToken && Topics.IsValidDeviceId(request.DeviceIdHeader)
            ? request.DeviceIdHeader
            : null;
    }
}
