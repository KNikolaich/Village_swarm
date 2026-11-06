using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Modules.IpCams;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>A camera on its own, without an NVR (contracts/ipcam.md): ONVIF (or Dahua's API), a top-level device of the swarm.</summary>
[Collection(HiveCollection.Name)]
public sealed class IpCamStandaloneTests(HiveFixture hive) : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _fakes = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        using var admin = await hive.LoginAsync();
        (await admin.PutAsJsonAsync("/api/modes/armed", new { armed = false })).Dispose();
        foreach (var nvr in (await admin.GetFromJsonAsync<List<NvrDto>>("/api/ipcams"))!)
            (await admin.DeleteAsync($"/api/ipcams/{nvr.Id}")).Dispose();
        foreach (var fake in _fakes)
            await fake.DisposeAsync();
    }

    private async Task<FakeOnvifCamera> OnvifAsync(bool events = true)
    {
        var fake = await FakeOnvifCamera.StartAsync(events);
        _fakes.Add(fake);
        return fake;
    }

    private static async Task<ProbeResultDto> ProbeAsync(HttpClient admin, int port, string user, string password) =>
        (await (await admin.PostAsJsonAsync("/api/ipcams/probe", new NvrProbeRequest("127.0.0.1", port, user, password, NvrKinds.Camera)))
            .Content.ReadFromJsonAsync<ProbeResultDto>())!;

    private async Task<NvrDto> AddCameraAsync(HttpClient admin, int port, string user, string password, ProbeResultDto probe, string name,
        bool recordWhenDisarmed = false)
    {
        using var created = await admin.PostAsJsonAsync("/api/ipcams", new NvrRequest(name, "127.0.0.1", port, user, password,
            [new IpCamChannelRequest(1, name, RecordWhenDisarmed: recordWhenDisarmed)], Kind: NvrKinds.Camera, Vendor: probe.Vendor,
            SnapshotUrl: probe.SnapshotUrl, RtspMain: probe.RtspMain, RtspSub: probe.RtspSub, EventsUrl: probe.EventsUrl));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var camera = (await created.Content.ReadFromJsonAsync<NvrDto>())!;
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == camera.Channels[0].DeviceId && d.Status == DeviceStatus.Online), $"{name} online");
        return camera;
    }

    [Fact]
    public async Task An_onvif_camera_is_found_added_on_its_own_and_its_motion_brings_photos()
    {
        var fake = await OnvifAsync();
        using var admin = await hive.LoginAsync();
        var wrong = await ProbeAsync(admin, fake.Port, FakeOnvifCamera.User, "wrong");
        Assert.False(wrong.Ok);
        Assert.Equal("auth", wrong.Error);

        var probe = await ProbeAsync(admin, fake.Port, FakeOnvifCamera.User, FakeOnvifCamera.Password);
        Assert.True(probe.Ok, probe.Detail);
        Assert.Equal(CameraVendors.Onvif, probe.Vendor);
        Assert.Equal("Asecam 8MP-PTZ", probe.Model);
        Assert.Equal(CameraVendors.Onvif, probe.Motion);
        Assert.Equal($"http://127.0.0.1:{fake.Port}/snap.jpg", probe.SnapshotUrl); // its own idea of its address replaced by ours
        Assert.Equal("rtsp://127.0.0.1:554/main", probe.RtspMain);
        Assert.Equal("rtsp://127.0.0.1:554/sub", probe.RtspSub);

        var camera = await AddCameraAsync(admin, fake.Port, FakeOnvifCamera.User, FakeOnvifCamera.Password, probe, "Калитка");
        var cam = camera.Channels[0].DeviceId;
        var device = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == cam));
        Assert.Null(device.ParentId); // top-level, no NVR above it
        Assert.False(await hive.QueryAsync(db => db.Devices.AnyAsync(d => d.DeviceId == camera.DeviceId)));
        var listed = (await admin.GetFromJsonAsync<List<NvrDto>>("/api/ipcams"))!.Single(n => n.Id == camera.Id);
        Assert.Equal(NvrKinds.Camera, listed.Kind);
        Assert.Equal(CameraVendors.Onvif, listed.Motion);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/modes/armed", new { armed = true })).StatusCode);
        fake.Motion();
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == cam && e.Type == "motion" && e.Severity == EventSeverity.Alarm), "alarm from ONVIF motion");
        var id = await hive.QueryAsync(db => db.Events.Where(e => e.DeviceId == cam && e.Type == "motion").Select(e => e.Id).SingleAsync());
        await hive.WaitForAsync(db => db.Media.CountAsync(m => m.EventId == id).ContinueWith(t => t.Result == 3), "three photos from the snapshot address");
        Assert.True(fake.Snapshots >= 3);

        using var sent = await admin.PostAsJsonAsync($"/api/devices/{cam}/commands", new { name = "snapshot" });
        var cid = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cid").GetString();
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.Cid == cid && c.Status == CommandStatus.Ok), "snapshot command");
    }

    [Fact]
    public async Task A_camera_without_events_works_for_snapshots_and_says_it_tells_no_motion()
    {
        var fake = await OnvifAsync(events: false);
        using var admin = await hive.LoginAsync();
        var probe = await ProbeAsync(admin, fake.Port, FakeOnvifCamera.User, FakeOnvifCamera.Password);
        Assert.True(probe.Ok, probe.Detail);
        Assert.Equal("none", probe.Motion);
        Assert.Null(probe.EventsUrl);

        var camera = await AddCameraAsync(admin, fake.Port, FakeOnvifCamera.User, FakeOnvifCamera.Password, probe, "Сарай");
        var listed = (await admin.GetFromJsonAsync<List<NvrDto>>("/api/ipcams"))!.Single(n => n.Id == camera.Id);
        Assert.Equal("none", listed.Motion);
        Assert.Equal(0, fake.Subscriptions);

        using var live = await admin.GetAsync($"/api/ipcams/{camera.Id}/snapshot?channel=1");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("cgi", live.Headers.GetValues("X-Snapshot-Via").Single());
    }

    [Fact]
    public async Task A_dahua_camera_on_its_own_is_found_by_its_http_api()
    {
        await using var fake = await FakeNvr.StartAsync();
        using var admin = await hive.LoginAsync();
        var probe = await ProbeAsync(admin, fake.Port, FakeNvr.User, FakeNvr.Password);
        Assert.True(probe.Ok, probe.Detail);
        Assert.Equal(CameraVendors.Dahua, probe.Vendor);
        Assert.Equal(CameraVendors.Dahua, probe.Motion);
        Assert.Equal([1], probe.Channels.Select(c => c.Channel));

        var camera = await AddCameraAsync(admin, fake.Port, FakeNvr.User, FakeNvr.Password, probe, "Дахуа в бане");
        fake.Motion(0);
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == camera.Channels[0].DeviceId && e.Type == "motion"), "motion from the Dahua camera");
        (await admin.DeleteAsync($"/api/ipcams/{camera.Id}")).Dispose();
    }

    [Fact]
    public async Task A_camera_without_any_frame_source_needs_a_stream_typed_in()
    {
        using var admin = await hive.LoginAsync();
        using var refused = await admin.PostAsJsonAsync("/api/ipcams", new NvrRequest("Пустая", "127.0.0.1", 81, "admin", "x",
            [new IpCamChannelRequest(1, "Пустая")], Kind: NvrKinds.Camera, Vendor: CameraVendors.Onvif));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var twoChannels = await admin.PostAsJsonAsync("/api/ipcams", new NvrRequest("Две", "127.0.0.1", 81, "admin", "x",
            [new IpCamChannelRequest(1, "a"), new IpCamChannelRequest(2, "b")], Kind: NvrKinds.Camera, Vendor: CameraVendors.Onvif, RtspMain: "rtsp://127.0.0.1/x"));
        Assert.Equal(HttpStatusCode.BadRequest, twoChannels.StatusCode);
    }
}
