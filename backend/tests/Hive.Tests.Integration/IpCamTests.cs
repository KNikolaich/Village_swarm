using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Modules.IpCams;
using Hive.Modules.Notify;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>IP cameras behind a Dahua NVR (contracts/ipcam.md), against a fake NVR: motion → event → photos → Telegram when armed.</summary>
[Collection(HiveCollection.Name)]
public sealed class IpCamTests(HiveFixture hive) : IAsyncLifetime
{
    private FakeNvr _nvr = null!;
    private FakeTelegram Tg => hive.Telegram;

    public async Task InitializeAsync() => _nvr = await FakeNvr.StartAsync();

    public async Task DisposeAsync()
    {
        // The guard is shared by the whole collection: leave it off.
        using var admin = await hive.LoginAsync();
        (await admin.PutAsJsonAsync("/api/modes/armed", new { armed = false })).Dispose();
        foreach (var nvr in (await admin.GetFromJsonAsync<List<NvrDto>>("/api/ipcams"))!)
            (await admin.DeleteAsync($"/api/ipcams/{nvr.Id}")).Dispose();
        await _nvr.DisposeAsync();
    }

    private async Task LinkAsync(long chat)
    {
        using var client = await hive.LoginAsync();
        var code = await (await client.PostAsync("/api/me/telegram/code", null)).Content.ReadFromJsonAsync<LinkCodeDto>();
        Tg.UserSays(chat, $"/link {code!.Code}");
        await Tg.WaitForAsync(s => s.ChatId == chat && s.Text?.Contains("Чат привязан") == true, "link confirmation");
    }

    private async Task<NvrDto> AddNvrAsync(HttpClient admin, string name, params IpCamChannelRequest[] channels)
    {
        using var created = await admin.PostAsJsonAsync("/api/ipcams",
            new NvrRequest(name, "127.0.0.1", _nvr.Port, FakeNvr.User, FakeNvr.Password, channels));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var nvr = (await created.Content.ReadFromJsonAsync<NvrDto>())!;
        foreach (var c in nvr.Channels)
            await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == c.DeviceId && d.Status == DeviceStatus.Online), $"{c.DeviceId} online");
        return nvr;
    }

    [Fact]
    public async Task Probe_checks_the_login_and_lists_the_channels_and_the_password_never_comes_back()
    {
        using var admin = await hive.LoginAsync();
        var wrong = await (await admin.PostAsJsonAsync("/api/ipcams/probe", new NvrProbeRequest("127.0.0.1", _nvr.Port, FakeNvr.User, "wrong")))
            .Content.ReadFromJsonAsync<ProbeResultDto>();
        Assert.False(wrong!.Ok);
        Assert.Equal("auth", wrong.Error);

        var right = await (await admin.PostAsJsonAsync("/api/ipcams/probe", new NvrProbeRequest($"http://127.0.0.1/", _nvr.Port, FakeNvr.User, FakeNvr.Password)))
            .Content.ReadFromJsonAsync<ProbeResultDto>();
        Assert.True(right!.Ok, right.Detail);
        Assert.Equal("DHI-NVR4104HS-P-4KS2", right.DeviceType);
        Assert.Equal([1, 2, 3], right.Channels.Select(c => c.Channel));
        Assert.Equal("Ворота", right.Channels[0].Title);
        Assert.False(right.Channels[2].Connected);
        Assert.Equal("rtsp://127.0.0.1/cam/realmonitor?channel=2&subtype=1", right.Channels[1].RtspUrl);

        var nvr = await AddNvrAsync(admin, "Регистратор", new IpCamChannelRequest(1, "Ворота"));
        var raw = await admin.GetStringAsync("/api/ipcams");
        Assert.DoesNotContain(FakeNvr.Password, raw);
        Assert.True((await admin.GetFromJsonAsync<List<NvrDto>>("/api/ipcams"))!.Single(n => n.Id == nvr.Id).PasswordSet);
        var stored = await hive.QueryAsync(db => db.Nvrs.SingleAsync(n => n.Id == nvr.Id));
        Assert.DoesNotContain(FakeNvr.Password, stored.PasswordProtected);

        var probed = await (await admin.PostAsync($"/api/ipcams/{nvr.Id}/probe", null)).Content.ReadFromJsonAsync<ProbeResultDto>();
        Assert.True(probed!.Ok);
        using var live = await admin.GetAsync($"/api/ipcams/{nvr.Id}/snapshot?channel=1");
        Assert.Equal("image/jpeg", live.Content.Headers.ContentType?.MediaType);

        var device = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == nvr.Channels[0].DeviceId));
        Assert.Equal(Drivers.IpCam, device.Driver);
        Assert.Equal(Cameras.IpCam, device.TypeCode);
        Assert.Equal("Ворота", device.Name);

        // A viewer sees the swarm but not the NVR's settings.
        await hive.CreateUserAsync("ipcam-viewer", "viewer-password-1", "viewer");
        using var viewer = await hive.LoginAsync("ipcam-viewer", "viewer-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/ipcams")).StatusCode);
    }

    [Fact]
    public async Task Motion_raises_an_alarm_with_three_photos_only_when_armed()
    {
        const long chat = 900_500;
        await LinkAsync(chat);
        using var admin = await hive.LoginAsync();
        var nvr = await AddNvrAsync(admin, "Дача",
            new IpCamChannelRequest(1, "Ворота"), new IpCamChannelRequest(2, "Двор"), new IpCamChannelRequest(3, "Сад"));
        var gate = nvr.Channels[0].DeviceId;
        var yard = nvr.Channels[1].DeviceId;
        var garden = nvr.Channels[2].DeviceId;

        // Armed: alarm with sound, then the three NVR frames as a reply.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/modes/armed", new { armed = true })).StatusCode);
        _nvr.Motion(0);
        var alarm = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "sendmessage" && s.Text?.Contains(gate) == true, "alarm text");
        Assert.Contains("Движение", alarm.Text);
        Assert.False(alarm.Silent);
        var album = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "sendmediagroup" && s.ReplyTo == alarm.MessageId, "photos as a reply");
        Assert.Equal(3, album.Photos);
        var motion = await hive.QueryAsync(db => db.Events.SingleAsync(e => e.DeviceId == gate && e.Type == "motion"));
        Assert.Equal(EventSeverity.Alarm, motion.Severity);
        Assert.True(motion.Armed);
        var photos = await hive.QueryAsync(db => db.Media.Where(m => m.EventId == motion.Id).OrderBy(m => m.Id).ToListAsync());
        Assert.Equal([$"{motion.Id}-1", $"{motion.Id}-2", $"{motion.Id}-3"], photos.Select(p => p.Id));
        Assert.All(photos, p => Assert.Equal("nvr", p.UploadedVia));
        Assert.All(photos, p => Assert.True(Math.Abs((p.Ts - DateTimeOffset.UtcNow).TotalMinutes) < 5)); // hive time, not the NVR's

        // The same camera again within the cooldown: nothing new.
        _nvr.Motion(0);
        await Task.Delay(2500);
        Assert.Equal(1, await hive.QueryAsync(db => db.Events.CountAsync(e => e.DeviceId == gate && e.Type == "motion")));

        // Arriving: a quiet warning, photos kept but not pushed.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/modes/armed", new { armed = true, mode = "arriving" })).StatusCode);
        _nvr.Motion(2);
        var warn = await Tg.WaitForAsync(s => s.ChatId == chat && s.Method == "sendmessage" && s.Text?.Contains(garden) == true, "arriving warning");
        Assert.True(warn.Silent);
        var arriving = await hive.QueryAsync(db => db.Events.SingleAsync(e => e.DeviceId == garden && e.Type == "motion"));
        Assert.Equal(EventSeverity.Warn, arriving.Severity);
        await hive.WaitForAsync(db => db.Media.CountAsync(m => m.EventId == arriving.Id).ContinueWith(t => t.Result == 3), "arriving photos stored");

        // Disarmed: the feed only, no photos, nothing in Telegram.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/modes/armed", new { armed = false })).StatusCode);
        var snapshotsBefore = _nvr.Snapshots;
        _nvr.Motion(1);
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == yard && e.Type == "motion"), "disarmed motion in the feed");
        var quiet = await hive.QueryAsync(db => db.Events.SingleAsync(e => e.DeviceId == yard && e.Type == "motion"));
        Assert.Equal(EventSeverity.Info, quiet.Severity);
        Assert.False(quiet.Armed);
        await Task.Delay(3000);
        Assert.Equal(0, await hive.QueryAsync(db => db.Media.CountAsync(m => m.EventId == quiet.Id)));
        Assert.Equal(snapshotsBefore, _nvr.Snapshots);
        Assert.DoesNotContain(Tg.Outbox, s => s.ChatId == chat && s.Text?.Contains(yard) == true);
        Assert.Empty(JsonSerializer.Deserialize<string[]>(quiet.Payload.RootElement.GetProperty("photos").GetRawText())!);
    }

    [Fact]
    public async Task Record_when_disarmed_keeps_photos_without_an_alarm()
    {
        using var admin = await hive.LoginAsync();
        var nvr = await AddNvrAsync(admin, "Склад", new IpCamChannelRequest(2, "Сарай", RecordWhenDisarmed: true));
        var shed = nvr.Channels[0].DeviceId;
        _nvr.Motion(1);
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == shed && e.Type == "motion" && e.Severity == EventSeverity.Info), "motion in the feed");
        var id = await hive.QueryAsync(db => db.Events.Where(e => e.DeviceId == shed).Select(e => e.Id).SingleAsync());
        await hive.WaitForAsync(db => db.Media.CountAsync(m => m.EventId == id).ContinueWith(t => t.Result == 3), "photos kept while disarmed");
    }

    [Fact]
    public async Task Snapshot_command_takes_a_frame_and_other_commands_are_refused()
    {
        using var admin = await hive.LoginAsync();
        var nvr = await AddNvrAsync(admin, "Офис", new IpCamChannelRequest(1, "Вход"));
        var cam = nvr.Channels[0].DeviceId;

        using var sent = await admin.PostAsJsonAsync($"/api/devices/{cam}/commands", new { name = "snapshot" });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var cid = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cid").GetString();
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.Cid == cid && c.Status == CommandStatus.Ok), "snapshot command ok");
        Assert.True(await hive.QueryAsync(db => db.Media.AnyAsync(m => m.DeviceId == cam && m.Kind == MediaKind.Snapshot)));

        using var relay = await admin.PostAsJsonAsync($"/api/devices/{cam}/commands", new { name = "relay", payload = new { channel = "relay", set = "on" } });
        Assert.Equal(HttpStatusCode.BadRequest, relay.StatusCode);
    }

    [Fact]
    public async Task Cameras_go_offline_when_the_nvr_is_gone_and_back_when_it_returns()
    {
        using var admin = await hive.LoginAsync();
        var nvr = await AddNvrAsync(admin, "Подвал", new IpCamChannelRequest(1, "Подвал"));
        var cam = nvr.Channels[0].DeviceId;

        _nvr.GoDown();
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == cam && d.Status == DeviceStatus.Offline), "offline with the NVR");
        _nvr.ComeBack();
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == cam && d.Status == DeviceStatus.Online), "online again", timeoutS: 30);
    }
}
