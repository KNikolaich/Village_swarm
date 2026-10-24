using System.Net;
using System.Net.Http.Json;
using Hive.Domain;
using Hive.Modules.IpCams;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>Live video (contracts/ipcam.md "Live video"): the hive keeps go2rtc's streams in line and guards the way to them.</summary>
[Collection(HiveCollection.Name)]
public sealed class VideoTests(HiveFixture hive) : IAsyncLifetime
{
    private FakeNvr _nvr = null!;
    private FakeGo2Rtc Go2Rtc => hive.Go2Rtc;

    public async Task InitializeAsync() => _nvr = await FakeNvr.StartAsync();

    public async Task DisposeAsync()
    {
        using var admin = await hive.LoginAsync();
        foreach (var nvr in (await admin.GetFromJsonAsync<List<NvrDto>>("/api/ipcams"))!)
            (await admin.DeleteAsync($"/api/ipcams/{nvr.Id}")).Dispose();
        await _nvr.DisposeAsync();
    }

    private static async Task WaitAsync(Func<bool> condition, string what, int timeoutS = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutS);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for: {what}");
            await Task.Delay(200);
        }
    }

    private async Task<NvrDto> AddNvrAsync(HttpClient admin)
    {
        using var created = await admin.PostAsJsonAsync("/api/ipcams",
            new NvrRequest("Видео", "127.0.0.1", _nvr.Port, FakeNvr.User, FakeNvr.Password, [new IpCamChannelRequest(2, "Двор")]));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<NvrDto>())!;
    }

    [Fact]
    public async Task Each_camera_gets_two_streams_in_go2rtc_kept_there_and_removed_with_it()
    {
        using var admin = await hive.LoginAsync();
        var nvr = await AddNvrAsync(admin);
        var cam = nvr.Channels[0].DeviceId;

        await WaitAsync(() => Go2Rtc.Streams.ContainsKey($"{cam}-sub") && Go2Rtc.Streams.ContainsKey($"{cam}-main"), "both streams pushed");
        Assert.Equal("rtsp://admin:nvr-secret-1@127.0.0.1:554/cam/realmonitor?channel=2&subtype=1", Go2Rtc.Streams[$"{cam}-sub"]);
        Assert.Equal("rtsp://admin:nvr-secret-1@127.0.0.1:554/cam/realmonitor?channel=2&subtype=0", Go2Rtc.Streams[$"{cam}-main"]);

        // The widget's list: names to ask Caddy for, never an address or a password.
        var raw = await admin.GetStringAsync("/api/video/streams");
        Assert.DoesNotContain(FakeNvr.Password, raw);
        Assert.DoesNotContain("rtsp://", raw);
        var list = (await admin.GetFromJsonAsync<VideoStreamsDto>("/api/video/streams"))!;
        Assert.True(list.Enabled);
        var camera = Assert.Single(list.Cameras, c => c.DeviceId == cam);
        Assert.Equal(($"{cam}-sub", $"{cam}-main", 2), (camera.Sub, camera.Main, camera.Channel));

        // go2rtc restarted and forgot them: they come back without anyone touching the settings.
        Go2Rtc.Restart();
        await WaitAsync(() => Go2Rtc.Streams.ContainsKey($"{cam}-sub"), "streams back after a go2rtc restart");

        (await admin.DeleteAsync($"/api/ipcams/{nvr.Id}")).Dispose();
        await WaitAsync(() => !Go2Rtc.Streams.Keys.Any(k => k.StartsWith(cam, StringComparison.Ordinal)), "streams gone with the camera");
    }

    [Fact]
    public async Task Caddy_lets_through_only_signed_in_users_and_only_streams_of_the_list()
    {
        using var admin = await hive.LoginAsync();
        var nvr = await AddNvrAsync(admin);
        var cam = nvr.Channels[0].DeviceId;
        await WaitAsync(() => Go2Rtc.Streams.ContainsKey($"{cam}-sub"), "stream pushed");

        async Task<HttpStatusCode> AskAsync(HttpClient client, string uri)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/video/authorize");
            request.Headers.Add("X-Forwarded-Uri", uri);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.NoContent, await AskAsync(admin, $"/api/ws?src={cam}-sub"));
        Assert.Equal(HttpStatusCode.NoContent, await AskAsync(admin, $"/api/frame.jpeg?src={cam}-main"));
        Assert.Equal(HttpStatusCode.Forbidden, await AskAsync(admin, "/api/ws?src=ipcam999-ch1-sub"));
        // go2rtc would open any address given as src: never through the hive.
        Assert.Equal(HttpStatusCode.Forbidden, await AskAsync(admin, "/api/ws?src=" + Uri.EscapeDataString("rtsp://evil/x")));
        Assert.Equal(HttpStatusCode.Forbidden, await AskAsync(admin, $"/api/ws?src={cam}-sub&src=rtsp://evil/x"));
        Assert.Equal(HttpStatusCode.Forbidden, await AskAsync(admin, "/api/streams"));

        using var anonymous = hive.App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await AskAsync(anonymous, $"/api/ws?src={cam}-sub"));

        // A viewer may watch: live video is for everyone of the family who can see the cameras.
        await hive.CreateUserAsync("video-viewer", "viewer-password-2", "viewer");
        using var viewer = await hive.LoginAsync("video-viewer", "viewer-password-2");
        Assert.Equal(HttpStatusCode.NoContent, await AskAsync(viewer, $"/api/ws?src={cam}-sub"));
    }
}
