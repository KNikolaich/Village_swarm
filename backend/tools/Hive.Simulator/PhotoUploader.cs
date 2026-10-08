using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Hive.Simulator.Roles;

namespace Hive.Simulator;

public enum UploadOutcome
{
    Uploaded,

    /// <summary>Network or server trouble: keep the photo and try again later.</summary>
    Retry,

    /// <summary>The hive refused this photo (bad request): retrying will not help.</summary>
    Rejected,
}

/// <summary>POST {upload}/api/ingest/photo exactly as the guard-cam firmware does it (spec 5.4).</summary>
public static class PhotoUploader
{
    public static async Task<(UploadOutcome Outcome, string? Detail)> UploadAsync(
        HttpClient http, string deviceId, PhotoUpload upload, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{upload.Url.TrimEnd('/')}/api/ingest/photo")
        {
            Content = new ByteArrayContent(upload.Jpeg) { Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") } },
        };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Photo-Id", upload.PhotoId);
        request.Headers.Add("X-Event-Id", upload.EventId);
        request.Headers.Add("X-Event-Type", upload.EventType);
        request.Headers.Add("X-Ts", upload.Ts.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Device-Id", deviceId); // used only by a development hive without upload tokens

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return (UploadOutcome.Uploaded, null);
            var retry = (int)response.StatusCode >= 500
                || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
            return (retry ? UploadOutcome.Retry : UploadOutcome.Rejected, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (UploadOutcome.Retry, ex.Message);
        }
    }
}
