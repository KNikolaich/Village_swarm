using System.Net;
using System.Net.Http.Json;
using Hive.Domain;
using Hive.Modules.IpCams;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>PTZ of a camera on its own (contracts/ipcam.md "PTZ"): ONVIF and Dahua, a move that always ends, quiet motion while turning.</summary>
[Collection(HiveCollection.Name)]
public sealed class IpCamPtzTests(HiveFixture hive) : IAsyncLifetime
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

    private async Task<string> AddCameraAsync(HttpClient admin, int port, string user, string password, string name)
    {
        using var probed = await admin.PostAsJsonAsync("/api/ipcams/probe", new NvrProbeRequest("127.0.0.1", port, user, password, NvrKinds.Camera));
        var probe = (await probed.Content.ReadFromJsonAsync<ProbeResultDto>())!;
        Assert.True(probe.Ok, probe.Detail);
        using var created = await admin.PostAsJsonAsync("/api/ipcams", new NvrRequest(name, "127.0.0.1", port, user, password,
            [new IpCamChannelRequest(1, name)], Kind: NvrKinds.Camera, Vendor: probe.Vendor,
            SnapshotUrl: probe.SnapshotUrl, RtspMain: probe.RtspMain, RtspSub: probe.RtspSub, EventsUrl: probe.EventsUrl));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var device = (await created.Content.ReadFromJsonAsync<NvrDto>())!.Channels[0].DeviceId;
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == device && d.Status == DeviceStatus.Online), $"{name} online");
        return device;
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string device, object command)
    {
        using var response = await client.PostAsJsonAsync($"/api/ipcams/ptz/{device}", command);
        return response.StatusCode;
    }

    private static async Task WaitForCallAsync(IEnumerable<string> calls, string call, TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        while (!calls.Contains(call))
        {
            Assert.True(DateTime.UtcNow < until, $"no \"{call}\" in time; calls: {string.Join(", ", calls)}");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task An_onvif_camera_turns_stops_by_itself_goes_to_presets_and_keeps_quiet_while_turning()
    {
        var fake = await FakeOnvifCamera.StartAsync(ptz: true);
        _fakes.Add(fake);
        using var admin = await hive.LoginAsync();
        var cam = await AddCameraAsync(admin, fake.Port, FakeOnvifCamera.User, FakeOnvifCamera.Password, "Поворотная");

        var state = (await admin.GetFromJsonAsync<PtzStateDto>($"/api/ipcams/ptz/{cam}"))!;
        Assert.True(state.Available, state.Error);
        Assert.Equal([new PtzPresetDto("1", "Ворота"), new PtzPresetDto("2", "2")], state.Presets);

        // A move goes to the profile with the PTZ configuration and lasts a second unless repeated: then the hive stops it.
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(admin, cam, new { op = "move", pan = 0.5 }));
        Assert.Equal("move 0.5 0 - PT1S sub", fake.PtzCalls.First());
        await WaitForCallAsync(fake.PtzCalls, "stop", TimeSpan.FromSeconds(4));

        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(admin, cam, new { op = "move", zoom = -1 }));
        Assert.Contains("move - - -1 PT1S sub", fake.PtzCalls);
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(admin, cam, new { op = "stop" }));
        Assert.Equal(2, fake.PtzCalls.Count(c => c == "stop"));

        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(admin, cam, new { op = "preset", preset = "1" }));
        Assert.Contains("goto 1", fake.PtzCalls);
        Assert.True(await hive.QueryAsync(db => db.Audit.AnyAsync(a => a.Action == "ipcam.ptz.preset" && a.Target == cam)));

        // The camera just turned: the motion its detector reports is the picture moving, not an alarm.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/modes/armed", new { armed = true })).StatusCode);
        fake.Motion();
        await Task.Delay(2500);
        Assert.False(await hive.QueryAsync(db => db.Events.AnyAsync(e => e.DeviceId == cam && e.Type == "motion")));

        Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(admin, cam, new { op = "move", pan = 2 }));
        Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(admin, cam, new { op = "move" }));
        Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(admin, cam, new { op = "spin" }));
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(admin, "no-such-camera", new { op = "stop" }));
    }

    [Fact]
    public async Task A_camera_without_ptz_offers_no_controls_and_refuses_moves()
    {
        var fake = await FakeOnvifCamera.StartAsync();
        _fakes.Add(fake);
        using var admin = await hive.LoginAsync();
        var cam = await AddCameraAsync(admin, fake.Port, FakeOnvifCamera.User, FakeOnvifCamera.Password, "Неподвижная");

        var state = (await admin.GetFromJsonAsync<PtzStateDto>($"/api/ipcams/ptz/{cam}"))!;
        Assert.False(state.Available);
        Assert.Null(state.Error);
        Assert.Equal(HttpStatusCode.Conflict, await SendAsync(admin, cam, new { op = "move", pan = 1 }));
        Assert.Empty(fake.PtzCalls);
    }

    [Fact]
    public async Task A_dahua_camera_turns_through_ptz_cgi_and_stops_the_direction_it_moves_in()
    {
        var fake = await FakeNvr.StartAsync();
        _fakes.Add(fake);
        fake.Ptz = true;
        using var admin = await hive.LoginAsync();
        var cam = await AddCameraAsync(admin, fake.Port, FakeNvr.User, FakeNvr.Password, "Дахуа поворотная");

        var state = (await admin.GetFromJsonAsync<PtzStateDto>($"/api/ipcams/ptz/{cam}"))!;
        Assert.True(state.Available, state.Error);
        Assert.Equal([new PtzPresetDto("1", "Ворота"), new PtzPresetDto("3", "3")], state.Presets);

        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(admin, cam, new { op = "move", pan = -1, tilt = 0.5 }));
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(admin, cam, new { op = "move", pan = -1 }));
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(admin, cam, new { op = "stop" }));
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(admin, cam, new { op = "preset", preset = "3" }));
        Assert.Equal(["start LeftUp 4 8", "stop LeftUp 0 0", "start Left 0 8", "stop Left 0 0", "start GotoPreset 0 3"], fake.PtzCalls);

        // Released without a stop (the tab closed): Dahua has no timeout of its own, the hive stops it.
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(admin, cam, new { op = "move", zoom = 0.25 }));
        await WaitForCallAsync(fake.PtzCalls, "stop ZoomTele 0 0", TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task A_viewer_cannot_turn_a_camera()
    {
        await hive.CreateUserAsync("viewer-ptz", "viewer-ptz-password", Hive.Infrastructure.Identity.HiveRoles.Viewer);
        using var viewer = await hive.LoginAsync("viewer-ptz", "viewer-ptz-password");
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(viewer, "ipcam1-ch1", new { op = "stop" }));
    }
}
