using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Hive.Contracts;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Cluster;
using Hive.Modules.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Hive.Tests.Integration;

internal static class TestPhotos
{
    /// <summary>A small real JPEG, different for every seed.</summary>
    public static byte[] Jpeg(byte seed)
    {
        using var bitmap = new SKBitmap(16, 12);
        bitmap.Erase(new SKColor(seed, (byte)(255 - seed), 128));
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}

/// <summary>The city keeps the photos home sends: once, only with the key of the two nodes, and only the bytes that were named.</summary>
public sealed class PhotoReplicaReceiverTests(CityFixture city) : IClassFixture<CityFixture>
{
    private static HttpRequestMessage Put(string id, byte[] body, string? key, byte[]? namedBytes = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/ingest/replica/media/{id}") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        if (key is not null)
            request.Headers.Add("X-Replica-Key", key);
        request.Headers.Add("X-Device-Id", "guard-replica1");
        request.Headers.Add("X-Kind", "Photo");
        request.Headers.Add("X-Ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        request.Headers.Add("X-Sha256", TestPhotos.Sha(namedBytes ?? body));
        request.Headers.Add("X-Event-Id", Ulid.New());
        request.Headers.Add("X-Event-Type", "motion");
        return request;
    }

    [Fact]
    public async Task A_photo_from_home_is_kept_once_and_served_and_a_wrong_key_or_other_bytes_are_refused()
    {
        var key = PhotoReplicaPolicy.Key(CityFixture.CityBridgeSecret);
        var jpeg = TestPhotos.Jpeg(10);
        var id = $"{Ulid.New()}-0";
        var anonymous = city.App.CreateClient();

        // the city is an observer here: a machine endpoint still gets through, with the key
        using var created = await anonymous.SendAsync(Put(id, jpeg, key));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var row = await city.QueryAsync(db => db.Media.AsNoTracking().SingleAsync(m => m.Id == id));
        Assert.Equal(("guard-replica1", MediaKind.Photo, "replica", TestPhotos.Sha(jpeg)), (row.DeviceId, row.Kind, row.UploadedVia, row.Sha256));

        var admin = await city.LoginAsync();
        Assert.Equal(jpeg, await admin.GetByteArrayAsync($"/api/media/{id}"));

        // the same photo again is the same photo
        using var again = await anonymous.SendAsync(Put(id, jpeg, key));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await city.QueryAsync(db => db.Media.CountAsync(m => m.Id == id)));

        // no key, a wrong key
        using var none = await anonymous.SendAsync(Put($"{Ulid.New()}-0", jpeg, null));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        using var wrong = await anonymous.SendAsync(Put($"{Ulid.New()}-0", jpeg, PhotoReplicaPolicy.Key("not-the-secret")));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // bytes that are not the photo that was named, bytes that are no photo at all, a name that is no photo id
        var otherId = $"{Ulid.New()}-0";
        using var swapped = await anonymous.SendAsync(Put(otherId, TestPhotos.Jpeg(99), key, namedBytes: jpeg));
        Assert.Equal(HttpStatusCode.BadRequest, swapped.StatusCode);
        using var junk = await anonymous.SendAsync(Put($"{Ulid.New()}-0", [1, 2, 3, 4, 5, 6], key));
        Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);
        using var badId = await anonymous.SendAsync(Put("not-a-photo-id", jpeg, key));
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        Assert.False(await city.QueryAsync(db => db.Media.AnyAsync(m => m.Id == otherId)));
    }
}

/// <summary>A city that only writes down what home sends it.</summary>
internal sealed class FakeCityReplica : IAsyncDisposable
{
    private readonly WebApplication _app;

    public ConcurrentQueue<(string Id, IReadOnlyDictionary<string, string> Headers, byte[] Body)> Received { get; } = new();

    public int Status { get; set; } = 201;

    public string BaseUrl { get; private set; } = "";

    private FakeCityReplica(WebApplication app) => _app = app;

    public static async Task<FakeCityReplica> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeCityReplica(app);
        app.MapPut("/api/ingest/replica/media/{id}", async (string id, HttpRequest request) =>
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer);
            fake.Received.Enqueue((id, request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()), buffer.ToArray()));
            return Results.StatusCode(fake.Status);
        });
        await app.StartAsync();
        fake.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return fake;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

/// <summary>Home sends the photos to the city, alarms first, and does not give up on the ones the city refuses or cannot take.</summary>
[Collection(HiveCollection.Name)]
public sealed class PhotoReplicaSenderTests(HiveFixture hive)
{
    private const string Secret = "shared-secret-of-home-and-city";

    private async Task<string> AddPhotoAsync(string name, MediaKind kind, EventSeverity? severity, TimeSpan age, string device = "guard-send1")
    {
        var jpeg = TestPhotos.Jpeg((byte)(name.Length * 17));
        var ts = DateTimeOffset.UtcNow - age;
        var id = $"{Ulid.New(ts)}-0";
        var path = $"photos/replica-send/{name}.jpg";
        var full = Path.Combine(hive.MediaRoot, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, jpeg);
        await hive.QueryAsync(async db =>
        {
            string? eventId = null;
            if (severity is { } s)
            {
                eventId = Ulid.New(ts);
                db.Events.Add(new DeviceEvent { Id = eventId, DeviceId = device, Type = "motion", Severity = s, Ts = ts, ReceivedAt = ts, Payload = System.Text.Json.JsonDocument.Parse("{}") });
            }
            db.Media.Add(new MediaItem { Id = id, EventId = eventId, DeviceId = device, Kind = kind, Ts = ts, ReceivedAt = ts, Path = path, Bytes = jpeg.Length, Sha256 = TestPhotos.Sha(jpeg) });
            await db.SaveChangesAsync();
            return 0;
        });
        return id;
    }

    private async Task SetCityAsync(string? url) => await hive.QueryAsync(async db =>
    {
        await db.Modes.Where(m => m.Key == PhotoReplicator.UrlKey).ExecuteDeleteAsync();
        if (url is not null)
        {
            db.Modes.Add(new Mode { Key = PhotoReplicator.UrlKey, Value = $$"""{"host":null,"port":1883,"replicaUrl":"{{url}}"}""", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        return 0;
    });

    private async Task ClearQueueAsync() => await hive.QueryAsync(async db =>
    {
        // what other tests left in the queue is of no interest here
        await db.Media.ExecuteUpdateAsync(s => s.SetProperty(m => m.ReplicatedAt, DateTimeOffset.UtcNow));
        return 0;
    });

    private PhotoReplicator Replicator(PhotoReplicaState state)
    {
        var scopes = hive.App.Services.GetRequiredService<IServiceScopeFactory>();
        var hiveOptions = Options.Create(new HiveOptions { Node = "home", Role = "active" });
        return new PhotoReplicator(Options.Create(new ClusterOptions { Enabled = true }), hiveOptions, Options.Create(new CityBrokerOptions { BridgePassword = Secret }),
            new NodeState(hiveOptions), scopes, hive.App.Services.GetRequiredService<MediaStore>(), state, TimeProvider.System, NullLogger<PhotoReplicator>.Instance);
    }

    [Fact]
    public async Task Alarm_photos_go_first_old_ones_stay_and_a_sent_photo_is_not_sent_twice()
    {
        await ClearQueueAsync();
        await using var city = await FakeCityReplica.StartAsync();
        await SetCityAsync(city.BaseUrl);
        var snapshot = await AddPhotoAsync("rs-snap", MediaKind.Snapshot, null, TimeSpan.FromMinutes(1));
        var info = await AddPhotoAsync("rs-info", MediaKind.Photo, EventSeverity.Info, TimeSpan.FromMinutes(2));
        var alarm = await AddPhotoAsync("rs-alarm-x", MediaKind.Photo, EventSeverity.Alarm, TimeSpan.FromMinutes(3));
        var staleSnapshot = await AddPhotoAsync("rs-stale", MediaKind.Snapshot, null, TimeSpan.FromHours(7));    // past its six hours
        var oldAlarm = await AddPhotoAsync("rs-oldalarm", MediaKind.Photo, EventSeverity.Alarm, TimeSpan.FromDays(8));   // past its week
        var state = new PhotoReplicaState();
        var replicator = Replicator(state);

        Assert.Equal(3, await replicator.TickAsync(CancellationToken.None));

        Assert.Equal([alarm, info, snapshot], city.Received.Select(r => r.Id).ToArray());
        var first = city.Received.First();
        Assert.Equal(PhotoReplicaPolicy.Key(Secret), first.Headers["X-Replica-Key"]);
        Assert.Equal(("guard-send1", "Photo", "motion"), (first.Headers["X-Device-Id"], first.Headers["X-Kind"], first.Headers["X-Event-Type"]));
        Assert.Equal(TestPhotos.Sha(first.Body), first.Headers["X-Sha256"]);
        Assert.NotNull(state.LastSentAt);

        var ids = new[] { alarm, info, snapshot, staleSnapshot, oldAlarm };
        var rows = await hive.QueryAsync(db => db.Media.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync());
        Assert.All(rows.Where(m => m.Id == alarm || m.Id == info || m.Id == snapshot), m => Assert.NotNull(m.ReplicatedAt));
        Assert.All(rows.Where(m => m.Id == staleSnapshot || m.Id == oldAlarm), m => Assert.Null(m.ReplicatedAt));

        Assert.Equal(0, await replicator.TickAsync(CancellationToken.None));   // nothing left to send

        // the status card: nothing waits that is still worth sending
        using var scope = hive.App.Services.CreateScope();
        var status = await PhotoReplicator.StatusAsync(scope.ServiceProvider.GetRequiredService<HiveDbContext>(), state, true, city.BaseUrl, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(0, status.Waiting);
    }

    [Fact]
    public async Task A_refused_photo_waits_longer_each_time_and_a_city_that_does_not_answer_stops_the_round()
    {
        await ClearQueueAsync();
        await using var city = await FakeCityReplica.StartAsync();
        await SetCityAsync(city.BaseUrl);
        var alarm = await AddPhotoAsync("rs-refused", MediaKind.Photo, EventSeverity.Alarm, TimeSpan.FromMinutes(1));
        var state = new PhotoReplicaState();
        var replicator = Replicator(state);

        city.Status = 422;   // for instance the city does not know the device yet
        Assert.Equal(0, await replicator.TickAsync(CancellationToken.None));
        var refused = await hive.QueryAsync(db => db.Media.AsNoTracking().SingleAsync(m => m.Id == alarm));
        Assert.Equal(1, refused.ReplicaTries);
        Assert.True(refused.ReplicaNextAt > DateTimeOffset.UtcNow);
        Assert.Contains("422", state.LastError);

        // in its pause the photo is left alone
        city.Received.Clear();
        Assert.Equal(0, await replicator.TickAsync(CancellationToken.None));
        Assert.Empty(city.Received);

        // the pause is over and the city takes it
        city.Status = 201;
        await hive.QueryAsync(async db =>
        {
            await db.Media.Where(m => m.Id == alarm).ExecuteUpdateAsync(s => s.SetProperty(m => m.ReplicaNextAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            return 0;
        });
        Assert.Equal(1, await replicator.TickAsync(CancellationToken.None));

        // a city nobody answers for: the photo is not counted as failed, and the round ends
        var next = await AddPhotoAsync("rs-nocity-xx", MediaKind.Photo, EventSeverity.Alarm, TimeSpan.FromMinutes(1));
        await SetCityAsync("http://127.0.0.1:1");
        Assert.Equal(0, await replicator.TickAsync(CancellationToken.None));
        var untouched = await hive.QueryAsync(db => db.Media.AsNoTracking().SingleAsync(m => m.Id == next));
        Assert.Equal(0, untouched.ReplicaTries);
        Assert.Null(untouched.ReplicatedAt);
        Assert.Contains("does not answer", state.LastError);

        await SetCityAsync(null);
        Assert.Equal(0, await replicator.TickAsync(CancellationToken.None));   // no city address: nothing is sent
    }
}
