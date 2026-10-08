using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Contracts;
using Hive.Modules.Events;
using Hive.Modules.Media;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Hive.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>Photo ingest and the events/media API (build step 5 acceptance).</summary>
[Collection(HiveCollection.Name)]
public sealed class MediaTests(HiveFixture hive) : IAsyncLifetime
{
    private static readonly byte[] TestJpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "scenarios", "photos", "frame0.jpg"));
    private readonly List<IHornetLink> _links = [];
    private HttpClient _http = null!;

    public async Task InitializeAsync() => _http = await hive.LoginAsync();

    public async Task DisposeAsync()
    {
        foreach (var link in _links)
            await link.DisposeAsync();
    }

    /// <summary>A guard-cam that publishes its motion event over MQTT; uploads go to the in-memory api.</summary>
    private async Task<(VirtualHornet Hornet, GuardCamRole Role, IHornetLink Link)> CameraAsync(string id)
    {
        var spec = new HornetSpec { Id = id, Type = "guard-cam", Motion = new MotionSpec { EverySeconds = 0, UploadUrl = "http://localhost" } };
        var role = (GuardCamRole)HornetRole.Create(spec, Path.Combine(AppContext.BaseDirectory, "scenarios", "photos"));
        var hornet = new VirtualHornet(spec, role);
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-10));
        var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        _links.Add(link);
        await link.ConnectAsync(id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        return (hornet, role, link);
    }

    private static async Task<string> MotionAsync(VirtualHornet hornet, GuardCamRole role, IHornetLink link, DateTimeOffset at)
    {
        role.TriggerMotion(at);
        var motion = hornet.TakePending(connected: true).Single(m => m.Topic.EndsWith("/event/motion", StringComparison.Ordinal));
        await link.PublishAsync(motion.Topic, motion.Payload, false, true, CancellationToken.None);
        return JsonDocument.Parse(motion.Payload).RootElement.GetProperty("id").GetString()!;
    }

    private HttpRequestMessage Upload(byte[] body, string photoId, string? deviceId, string? token = null, string contentType = "image/jpeg")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/photo")
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue(contentType) } },
        };
        request.Headers.Add("X-Photo-Id", photoId);
        request.Headers.Add("X-Ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (deviceId is not null)
            request.Headers.Add("X-Device-Id", deviceId);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Motion_photos_are_uploaded_once_and_served_by_event_and_by_day()
    {
        var (hornet, role, link) = await CameraAsync("guard-med1");
        var eventId = await MotionAsync(hornet, role, link, DateTimeOffset.UtcNow);

        var uploads = role.TakeUploads();
        Assert.Equal(3, uploads.Count);
        foreach (var upload in uploads)
            Assert.Equal(UploadOutcome.Uploaded, (await PhotoUploader.UploadAsync(_http, hornet.Id, upload, null, CancellationToken.None)).Outcome);
        // Re-upload after a lost response: 200, no duplicate.
        using (var again = await _http.SendAsync(Upload(uploads[0].Jpeg, uploads[0].PhotoId, hornet.Id)))
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.Id == eventId), "motion event ingested");
        var page = await _http.GetFromJsonAsync<Page<EventDto>>("/api/events?device=guard-med1");
        var evt = Assert.Single(page!.Items);
        Assert.Equal(("motion", "alarm"), (evt.Type, evt.Severity));
        Assert.Equal(uploads.Select(u => u.PhotoId), evt.Photos.Select(p => p.Id));

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow")).DateTime);
        var media = await _http.GetFromJsonAsync<Page<MediaDto>>($"/api/media?device=guard-med1&date={today:yyyy-MM-dd}");
        Assert.Equal(3, media!.Items.Count);
        Assert.All(media.Items, m => Assert.Equal(eventId, m.EventId));

        var days = await _http.GetFromJsonAsync<List<MediaDayDto>>($"/api/media/days?from={today.AddDays(-1):yyyy-MM-dd}&to={today:yyyy-MM-dd}&device=guard-med1");
        Assert.Equal([new MediaDayDto(today, 3)], days);

        var original = await _http.GetByteArrayAsync(media.Items[0].Url);
        Assert.Equal(uploads.Single(u => u.PhotoId == media.Items[0].Id).Jpeg, original);

        using var thumb = await _http.GetAsync(media.Items[0].ThumbUrl);
        Assert.Equal("image/webp", thumb.Content.Headers.ContentType?.MediaType);
        var webp = await thumb.Content.ReadAsByteArrayAsync();
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(webp, 8, 4));
        Assert.True(webp.Length < original.Length);
    }

    [Fact]
    public async Task Range_requests_are_supported()
    {
        var photoId = $"{Ulid.New()}-0";
        using (var created = await _http.SendAsync(Upload(TestJpeg, photoId, "guard-rng1")))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/media/{photoId}");
        request.Headers.Range = new RangeHeaderValue(0, 99);
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(100, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Bad_uploads_are_rejected()
    {
        var id = $"{Ulid.New()}-0";
        using (var r = await _http.SendAsync(Upload(TestJpeg, id, deviceId: null)))
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        using (var r = await _http.SendAsync(Upload(TestJpeg, id, "guard-bad1", token: "wrong-token")))
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        using (var r = await _http.SendAsync(Upload("not a jpeg at all"u8.ToArray(), id, "guard-bad1")))
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using (var r = await _http.SendAsync(Upload(TestJpeg, id, "guard-bad1", contentType: "image/png")))
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using (var r = await _http.SendAsync(Upload(TestJpeg, "photo-1", "guard-bad1")))
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var huge = new byte[2 * 1024 * 1024 + 10];
        TestJpeg.CopyTo(huge, 0);
        using (var r = await _http.SendAsync(Upload(huge, id, "guard-bad1")))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);

        Assert.False(await hive.QueryAsync(db => db.Media.AnyAsync(m => m.Id == id)));
    }

    [Fact]
    public async Task Upload_token_identifies_the_device()
    {
        await CameraAsync("guard-tok1");
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "guard-tok1"), "device registered");
        await hive.QueryAsync(db => db.Devices.Where(d => d.DeviceId == "guard-tok1")
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.UploadTokenHash, PhotoIngestService.HashToken("secret-token"))));

        var id = $"{Ulid.New()}-0";
        using (var r = await _http.SendAsync(Upload(TestJpeg, id, deviceId: "someone-else", token: "secret-token")))
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("guard-tok1", await hive.QueryAsync(db => db.Media.Where(m => m.Id == id).Select(m => m.DeviceId).SingleAsync()));
    }

    [Fact]
    public async Task Events_page_with_cursor_and_ack_once()
    {
        var (hornet, role, link) = await CameraAsync("guard-pag1");
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
            ids.Add(await MotionAsync(hornet, role, link, start.AddSeconds(30 * i)));
        await hive.WaitForAsync(db => db.Events.CountAsync(e => e.DeviceId == "guard-pag1").ContinueWith(c => c.Result == 5), "5 events");

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await _http.GetFromJsonAsync<Page<EventDto>>($"/api/events?device=guard-pag1&limit=2&cursor={cursor}");
            seen.AddRange(page!.Items.Select(e => e.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(Enumerable.Reverse(ids), seen); // newest first, nothing skipped or repeated

        var first = await (await _http.PostAsync($"/api/events/{ids[0]}/ack", null)).Content.ReadFromJsonAsync<EventDto>();
        var second = await (await _http.PostAsync($"/api/events/{ids[0]}/ack", null)).Content.ReadFromJsonAsync<EventDto>();
        Assert.NotNull(first!.AcknowledgedAt);
        Assert.Equal(first.AcknowledgedAt, second!.AcknowledgedAt);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/api/events/01J9Z3K7Q8M4N5P6R7S8T9V0WX/ack", null)).StatusCode);
    }

    [Fact]
    public async Task Devices_are_listed()
    {
        await CameraAsync("guard-lst1");
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "guard-lst1"), "device registered");
        var device = await _http.GetFromJsonAsync<Hive.Modules.Devices.DeviceDto>("/api/devices/guard-lst1");
        Assert.Equal(("guard-cam", "online"), (device!.Type, device.Status));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/devices/nope")).StatusCode);
    }
}
