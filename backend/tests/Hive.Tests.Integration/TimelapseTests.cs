using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Modules.Devices;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests.Integration;

/// <summary>Camera timelapse: light before the photo, light off whatever happens, the pace kept.</summary>
[Collection(HiveCollection.Name)]
public sealed class TimelapseTests(HiveFixture hive)
{
    private async Task RegisterAsync(string id, string type = "guard-cam")
    {
        var spec = new HornetSpec { Id = id, Type = type };
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"));
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        await link.ConnectAsync(id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == id && d.Status == DeviceStatus.Online), "device online");
    }

    private TimelapseWatch Watch() => hive.App.Services.GetServices<IHostedService>().OfType<TimelapseWatch>().Single();

    /// <summary>What the timelapse sent the camera, oldest first: "snapshot", "relay on", "relay off".</summary>
    private Task<List<string>> SentAsync(string id) => hive.QueryAsync(async db =>
        (await db.Commands.AsNoTracking().Where(c => c.DeviceId == id && c.IssuedBy == Timelapse.By).OrderBy(c => c.IssuedAt).ThenBy(c => c.Cid).ToListAsync())
        .Select(c => c.Name == "relay" ? $"relay {c.Payload.RootElement.GetProperty("set").GetString()}" : c.Name).ToList());

    private Task<TimelapseState?> StateAsync(string id) =>
        hive.QueryAsync(async db => TimelapseState.Read(await db.Devices.SingleAsync(d => d.DeviceId == id)));

    private Task ChangeAsync(string id, Func<TimelapseState, TimelapseState> change) => hive.QueryAsync(async db =>
    {
        var device = await db.Devices.SingleAsync(d => d.DeviceId == id);
        TimelapseState.Write(device, change(TimelapseState.Read(device)!));
        return await db.SaveChangesAsync();
    });

    /// <summary>The light had its warm-up: the snapshot is due.</summary>
    private Task WarmedUpAsync(string id) =>
        ChangeAsync(id, s => s with { Shot = s.Shot! with { AskedAt = s.Shot.AskedAt - Timelapse.LightWarmup } });

    private Task<int> PhotoArrivesAsync(string id, string photoId) => hive.QueryAsync(db =>
    {
        var now = DateTimeOffset.UtcNow;
        db.Media.Add(new MediaItem { Id = photoId, DeviceId = id, Kind = MediaKind.Snapshot, Ts = now, ReceivedAt = now, Path = $"test/{photoId}.jpg", Sha256 = photoId });
        return db.SaveChangesAsync();
    });

    [Fact]
    public async Task The_light_goes_on_before_the_photo_and_off_once_it_is_in()
    {
        await RegisterAsync("cam-tl-a");
        var admin = await hive.LoginAsync();
        using (var start = await admin.PutAsJsonAsync("/api/timelapse/cam-tl-a", new { intervalMin = 30, light = true }))
        {
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            var dto = await start.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(30, dto.GetProperty("intervalMin").GetInt32());
            Assert.True(dto.GetProperty("light").GetBoolean());
        }

        await Watch().CheckAsync(CancellationToken.None);
        Assert.Equal(["relay on"], await SentAsync("cam-tl-a"));
        var on = await hive.QueryAsync(db => db.Commands.SingleAsync(c => c.DeviceId == "cam-tl-a" && c.Name == "relay"));
        Assert.Equal(Timelapse.LightForS, on.Payload.RootElement.GetProperty("for_s").GetInt32());

        await Watch().CheckAsync(CancellationToken.None); // still warming up
        Assert.Equal(["relay on"], await SentAsync("cam-tl-a"));
        await WarmedUpAsync("cam-tl-a");
        await Watch().CheckAsync(CancellationToken.None);
        Assert.Equal(["relay on", "snapshot"], await SentAsync("cam-tl-a"));

        const string photo = "01JTIMELAPSE0000000000000A-0";
        await PhotoArrivesAsync("cam-tl-a", photo);
        await Watch().CheckAsync(CancellationToken.None);
        Assert.Equal(["relay on", "snapshot", "relay off"], await SentAsync("cam-tl-a"));
        Assert.Equal(MediaKind.Timelapse, await hive.QueryAsync(db => db.Media.Where(m => m.Id == photo).Select(m => m.Kind).SingleAsync()));
        var state = (await StateAsync("cam-tl-a"))!;
        Assert.Null(state.Shot);
        Assert.Equal(photo, state.LastPhotoId);
        Assert.Equal(state.StartedAt.AddMinutes(30), state.NextAt);

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/timelapse");
        Assert.Contains(list.EnumerateArray(), t => t.GetProperty("deviceId").GetString() == "cam-tl-a");
    }

    [Fact]
    public async Task A_photo_that_never_comes_switches_the_light_off_and_is_tried_again_in_a_minute()
    {
        await RegisterAsync("cam-tl-b");
        var admin = await hive.LoginAsync();
        using (var start = await admin.PutAsJsonAsync("/api/timelapse/cam-tl-b", new { intervalMin = 60, light = true }))
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        await Watch().CheckAsync(CancellationToken.None);
        await WarmedUpAsync("cam-tl-b");
        await Watch().CheckAsync(CancellationToken.None);
        Assert.Equal(["relay on", "snapshot"], await SentAsync("cam-tl-b"));

        await ChangeAsync("cam-tl-b", s => s with { Shot = s.Shot! with { SnapshotAt = s.Shot.SnapshotAt - Timelapse.PhotoTimeout } });
        await Watch().CheckAsync(CancellationToken.None);
        Assert.Equal(["relay on", "snapshot", "relay off"], await SentAsync("cam-tl-b"));
        var state = (await StateAsync("cam-tl-b"))!;
        Assert.Null(state.Shot);
        Assert.Equal("no_photo", state.LastError);
        Assert.Equal(1, state.Failures);
        Assert.InRange(state.NextAt - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(61));
    }

    [Fact]
    public async Task Stopping_during_a_shot_switches_the_light_off()
    {
        await RegisterAsync("cam-tl-c");
        var admin = await hive.LoginAsync();
        using (var start = await admin.PutAsJsonAsync("/api/timelapse/cam-tl-c", new { intervalMin = 5, light = true }))
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        await Watch().CheckAsync(CancellationToken.None);
        Assert.Equal(["relay on"], await SentAsync("cam-tl-c"));

        using (var stop = await admin.DeleteAsync("/api/timelapse/cam-tl-c"))
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        Assert.Equal(["relay on", "relay off"], await SentAsync("cam-tl-c"));
        Assert.Null(await StateAsync("cam-tl-c"));
        await Watch().CheckAsync(CancellationToken.None);
        Assert.Equal(["relay on", "relay off"], await SentAsync("cam-tl-c"));
        using var again = await admin.DeleteAsync("/api/timelapse/cam-tl-c");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task A_light_that_is_on_already_is_left_alone()
    {
        await RegisterAsync("cam-tl-d");
        await hive.QueryAsync(async db =>
        {
            var device = await db.Devices.SingleAsync(d => d.DeviceId == "cam-tl-d");
            device.State = JsonDocument.Parse("""{"relay":"on"}""");
            return await db.SaveChangesAsync();
        });
        var admin = await hive.LoginAsync();
        using (var start = await admin.PutAsJsonAsync("/api/timelapse/cam-tl-d", new { intervalMin = 15, light = true }))
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        await Watch().CheckAsync(CancellationToken.None);
        Assert.Equal(["snapshot"], await SentAsync("cam-tl-d"));
        await PhotoArrivesAsync("cam-tl-d", "01JTIMELAPSE0000000000000D-0");
        await Watch().CheckAsync(CancellationToken.None);
        Assert.Equal(["snapshot"], await SentAsync("cam-tl-d"));
    }

    [Fact]
    public async Task Odd_intervals_and_devices_without_a_camera_are_refused()
    {
        await RegisterAsync("meteo-tl-e", "meteo");
        var admin = await hive.LoginAsync();
        using (var odd = await admin.PutAsJsonAsync("/api/timelapse/meteo-tl-e", new { intervalMin = 7 }))
            Assert.Equal(HttpStatusCode.BadRequest, odd.StatusCode);
        using (var meteo = await admin.PutAsJsonAsync("/api/timelapse/meteo-tl-e", new { intervalMin = 60 }))
            Assert.Equal(HttpStatusCode.BadRequest, meteo.StatusCode);
        using (var nobody = await admin.PutAsJsonAsync("/api/timelapse/nobody-here", new { intervalMin = 60 }))
            Assert.Equal(HttpStatusCode.NotFound, nobody.StatusCode);
    }
}
