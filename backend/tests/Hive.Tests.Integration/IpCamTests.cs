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

    [Fact]
    public async Task A_channel_without_nvr_snapshots_gets_frames_from_its_rtsp_stream()
    {
        hive.Grabber.Reset();
        hive.Grabber.FailSubtypes["subtype=1"] = true; // the light sub-stream gives nothing here, the main stream does
        _nvr.NoSnapshot[3] = true;                      // and the NVR no snapshot of it, like the Asecam PTZ
        using var admin = await hive.LoginAsync();
        var nvr = await AddNvrAsync(admin, "Улица", new IpCamChannelRequest(3, "Зум 30"));
        var cam = nvr.Channels[0].DeviceId;

        using var sent = await admin.PostAsJsonAsync($"/api/devices/{cam}/commands", new { name = "snapshot" });
        var cid = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cid").GetString();
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.Cid == cid && c.Status == CommandStatus.Ok), "snapshot via RTSP");
        Assert.True(await hive.QueryAsync(db => db.Media.AnyAsync(m => m.DeviceId == cam && m.Kind == MediaKind.Snapshot)));
        Assert.Equal(
            ["rtsp://admin:nvr-secret-1@127.0.0.1:554/cam/realmonitor?channel=3&subtype=1", "rtsp://admin:nvr-secret-1@127.0.0.1:554/cam/realmonitor?channel=3&subtype=0"],
            hive.Grabber.Urls);
        Assert.Equal(SnapshotSources.Rtsp, await hive.QueryAsync(db => db.NvrChannels.Where(c => c.DeviceId == cam).Select(c => c.SnapshotVia).SingleAsync()));

        // Remembered: the next photo goes to the stream first and does not wait for snapshot.cgi.
        var cgiBefore = _nvr.Snapshots;
        using var again = await admin.PostAsJsonAsync($"/api/devices/{cam}/commands", new { name = "snapshot" });
        var cid2 = (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cid").GetString();
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.Cid == cid2 && c.Status == CommandStatus.Ok), "second snapshot");
        Assert.Equal(cgiBefore, _nvr.Snapshots);

        // The settings page shows where it came from; the password is in no answer.
        using var live = await admin.GetAsync($"/api/ipcams/{nvr.Id}/snapshot?channel=3");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("rtsp", live.Headers.GetValues("X-Snapshot-Via").Single());
        var listed = await admin.GetStringAsync("/api/ipcams");
        Assert.Contains("\"snapshotVia\":\"rtsp\"", listed);
        Assert.DoesNotContain(FakeNvr.Password, listed);
    }

    [Fact]
    public async Task Snapshot_failures_say_why_and_reach_the_cameras_journal()
    {
        hive.Grabber.Reset();
        _nvr.NoSnapshot[2] = true;
        using var admin = await hive.LoginAsync();
        var nvr = await AddNvrAsync(admin, "Только cgi", new IpCamChannelRequest(2, "Двор", SnapshotSource: SnapshotSources.Cgi));
        var cam = nvr.Channels[0].DeviceId;

        using var live = await admin.GetAsync($"/api/ipcams/{nvr.Id}/snapshot?channel=2");
        Assert.Equal(HttpStatusCode.BadGateway, live.StatusCode);
        var problem = await live.Content.ReadFromJsonAsync<JsonElement>();
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains("cgi: HTTP 200, text/plain", detail);
        Assert.Contains("Internal Error", detail);
        Assert.Empty(hive.Grabber.Urls); // cgi only: the stream is not tried

        using var sent = await admin.PostAsJsonAsync($"/api/devices/{cam}/commands", new { name = "snapshot" });
        var cid = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cid").GetString();
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.Cid == cid && c.Status == CommandStatus.Error), "snapshot command failed");
        var ack = await admin.GetFromJsonAsync<JsonElement>($"/api/devices/{cam}/commands/{cid}");
        Assert.Contains("Internal Error", ack.GetProperty("ack").GetProperty("error").GetString());
        await hive.WaitForAsync(db => db.DeviceLogs.AnyAsync(l => l.DeviceId == cam && l.Level == "warn" && l.Msg.StartsWith("snapshot failed")), "failure in the journal");

        // Switched to auto, the stream is tried after the NVR and gives the frame.
        using var update = await admin.PutAsJsonAsync($"/api/ipcams/{nvr.Id}",
            new NvrRequest("Только cgi", "127.0.0.1", _nvr.Port, FakeNvr.User, null, [new IpCamChannelRequest(2, "Двор", SnapshotSource: SnapshotSources.Auto)]));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var retry = await admin.GetAsync($"/api/ipcams/{nvr.Id}/snapshot?channel=2");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Single(hive.Grabber.Urls);
    }

    [Fact]
    public async Task A_locked_nvr_is_left_alone_until_it_unlocks_and_says_so()
    {
        hive.Grabber.Reset();
        _nvr.LockedFor = 1320;
        using var admin = await hive.LoginAsync();
        using var created = await admin.PostAsJsonAsync("/api/ipcams",
            new NvrRequest("Заблокирован", "127.0.0.1", _nvr.Port, FakeNvr.User, FakeNvr.Password, [new IpCamChannelRequest(1, "Ворота")]));
        var nvr = (await created.Content.ReadFromJsonAsync<NvrDto>())!;

        NvrDto? listed = null;
        for (var i = 0; i < 40 && listed?.Login.State != NvrLogins.Locked; i++)
        {
            await Task.Delay(250);
            listed = (await admin.GetFromJsonAsync<List<NvrDto>>("/api/ipcams"))!.Single(n => n.Id == nvr.Id);
        }
        Assert.Equal(NvrLogins.Locked, listed!.Login.State);
        Assert.InRange((listed.Login.Until!.Value - DateTimeOffset.UtcNow).TotalMinutes, 21, 24); // 1320 s and a margin

        // Nothing more goes to it: not the stream's reconnects, not a photo, not "Проверить", not ffmpeg.
        var seen = _nvr.LoginRequests;
        using var sent = await admin.PostAsJsonAsync($"/api/devices/{nvr.Channels[0].DeviceId}/commands", new { name = "snapshot" });
        var cid = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cid").GetString();
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.Cid == cid && c.Status == CommandStatus.Error), "snapshot refused while locked");
        var probe = await (await admin.PostAsync($"/api/ipcams/{nvr.Id}/probe", null)).Content.ReadFromJsonAsync<ProbeResultDto>();
        Assert.Equal("locked", probe!.Error);
        await Task.Delay(5000);
        Assert.Equal(seen, _nvr.LoginRequests);
        Assert.Empty(hive.Grabber.Urls);
    }

    [Fact]
    public async Task A_refused_password_is_tried_once_more_then_waits_for_the_settings()
    {
        using var admin = await hive.LoginAsync();
        using var created = await admin.PostAsJsonAsync("/api/ipcams",
            new NvrRequest("Старый пароль", "127.0.0.1", _nvr.Port, FakeNvr.User, "stale-password", [new IpCamChannelRequest(1, "Ворота")]));
        var nvr = (await created.Content.ReadFromJsonAsync<NvrDto>())!;

        NvrDto? listed = null;
        for (var i = 0; i < 60 && listed?.Login.State != NvrLogins.Paused; i++)
        {
            await Task.Delay(250);
            listed = (await admin.GetFromJsonAsync<List<NvrDto>>("/api/ipcams"))!.Single(n => n.Id == nvr.Id);
        }
        Assert.Equal(NvrLogins.Paused, listed!.Login.State);
        Assert.Equal(4, listed.Login.AttemptsLeft);
        var seen = _nvr.LoginRequests;
        Assert.InRange(seen, 2, 3); // the first try and one more after the pause, never a burst
        await Task.Delay(5000);
        Assert.Equal(seen, _nvr.LoginRequests);

        // The right password saved: back to work at once.
        using var fixedLogin = await admin.PutAsJsonAsync($"/api/ipcams/{nvr.Id}",
            new NvrRequest("Старый пароль", "127.0.0.1", _nvr.Port, FakeNvr.User, FakeNvr.Password, [new IpCamChannelRequest(1, "Ворота")]));
        Assert.Equal(HttpStatusCode.OK, fixedLogin.StatusCode);
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == nvr.Channels[0].DeviceId && d.Status == DeviceStatus.Online), "online with the new password");
    }

    [Fact]
    public async Task Snapshots_of_one_nvr_queue_one_at_a_time_and_a_second_press_joins_the_first()
    {
        _nvr.SnapshotDelayMs = 1500;
        using var admin = await hive.LoginAsync();
        var nvr = await AddNvrAsync(admin, "Медленный", new IpCamChannelRequest(1, "Ворота"), new IpCamChannelRequest(2, "Двор"));
        var gate = nvr.Channels[0].DeviceId;
        var before = _nvr.Snapshots;

        // Three presses on the same camera at once: one frame, three answers.
        var presses = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            using var sent = await admin.PostAsJsonAsync($"/api/devices/{gate}/commands", new { name = "snapshot" });
            return (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cid").GetString()!;
        }));
        foreach (var cid in presses)
            await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.Cid == cid && c.Status == CommandStatus.Ok), "every press answered");
        Assert.Equal(before + 1, _nvr.Snapshots);
        Assert.Equal(1, await hive.QueryAsync(db => db.Media.CountAsync(m => m.DeviceId == gate && m.Kind == MediaKind.Snapshot)));

        // Two cameras of the same NVR at once: asked one after the other, never together.
        var looks = await Task.WhenAll(new[] { 1, 2 }.Select(async ch =>
        {
            using var live = await admin.GetAsync($"/api/ipcams/{nvr.Id}/snapshot?channel={ch}");
            return live.StatusCode;
        }));
        Assert.All(looks, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(1, _nvr.MaxConcurrentSnapshots);

        // The settings page tells how it went.
        var shown = (await admin.GetFromJsonAsync<List<NvrDto>>("/api/ipcams"))!.Single(n => n.Id == nvr.Id);
        Assert.Equal("connected", shown.Stream.State);
        var last = shown.Channels[0].LastSnapshot!;
        Assert.True(last.Ok);
        Assert.Equal(SnapshotSources.Cgi, last.Via);
        Assert.Matches("^[0-9]+x[0-9]+$", last.Size!);
    }

    [Fact]
    public async Task Detailed_quality_asks_the_main_stream_first_and_motion_is_shown_in_the_settings()
    {
        hive.Grabber.Reset();
        using var admin = await hive.LoginAsync();
        var nvr = await AddNvrAsync(admin, "Подробно",
            new IpCamChannelRequest(1, "Ворота", SnapshotSource: SnapshotSources.Rtsp, SnapshotQuality: SnapshotQualities.Detailed));
        var cam = nvr.Channels[0].DeviceId;

        using var live = await admin.GetAsync($"/api/ipcams/{nvr.Id}/snapshot?channel=1");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.EndsWith("subtype=0", Assert.Single(hive.Grabber.Urls));

        _nvr.Motion(0);
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == cam && e.Type == "motion"), "motion in the feed");
        var shown = (await admin.GetFromJsonAsync<List<NvrDto>>("/api/ipcams"))!.Single(n => n.Id == nvr.Id);
        Assert.NotNull(shown.Channels[0].LastMotionAt);
        Assert.Equal(SnapshotQualities.Detailed, shown.Channels[0].SnapshotQuality);
    }

    [Fact]
    public async Task A_failed_press_shows_in_the_event_feed()
    {
        _nvr.NoSnapshot[1] = true;
        using var admin = await hive.LoginAsync();
        var nvr = await AddNvrAsync(admin, "Без снимков", new IpCamChannelRequest(1, "Ворота", SnapshotSource: SnapshotSources.Cgi));
        var cam = nvr.Channels[0].DeviceId;

        using var sent = await admin.PostAsJsonAsync($"/api/devices/{cam}/commands", new { name = "snapshot" });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == cam && e.Type == "photo_failed" && e.Severity == EventSeverity.Info), "photo_failed in the feed");
        var failed = await hive.QueryAsync(db => db.Events.SingleAsync(e => e.DeviceId == cam && e.Type == "photo_failed"));
        Assert.Contains("Internal Error", failed.Payload.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_grey_frame_is_refused_and_the_other_stream_gives_the_photo()
    {
        hive.Grabber.Reset();
        hive.Grabber.GreySubtypes["subtype=1"] = true; // the sub-stream decoded mid-GOP, as it was all night
        using var admin = await hive.LoginAsync();
        var nvr = await AddNvrAsync(admin, "Серый", new IpCamChannelRequest(1, "Ворота", SnapshotSource: SnapshotSources.Rtsp));
        var cam = nvr.Channels[0].DeviceId;

        using var sent = await admin.PostAsJsonAsync($"/api/devices/{cam}/commands", new { name = "snapshot" });
        var cid = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cid").GetString();
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.Cid == cid && c.Status == CommandStatus.Ok), "snapshot from the main stream");
        Assert.EndsWith("subtype=0", hive.Grabber.Urls.Last());
        var photo = await hive.QueryAsync(db => db.Media.SingleAsync(m => m.DeviceId == cam && m.Kind == MediaKind.Snapshot));
        var bytes = await File.ReadAllBytesAsync(Path.Combine(hive.MediaRoot, photo.Path));
        Assert.False(Hive.Modules.Media.ImageTools.MeasureFlatness(bytes)!.Grey);

        var shown = (await admin.GetFromJsonAsync<List<NvrDto>>("/api/ipcams"))!.Single(n => n.Id == nvr.Id);
        Assert.Contains("grey frame", shown.Channels[0].LastSnapshot!.Error);

        // Only grey from both streams: no photo stored at all, and the reason in the feed.
        hive.Grabber.GreySubtypes["subtype=0"] = true;
        using var again = await admin.PostAsJsonAsync($"/api/devices/{cam}/commands", new { name = "snapshot" });
        var cid2 = (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cid").GetString();
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.Cid == cid2 && c.Status == CommandStatus.Error), "grey only: refused");
        Assert.Equal(1, await hive.QueryAsync(db => db.Media.CountAsync(m => m.DeviceId == cam)));
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == cam && e.Type == "photo_failed"), "grey frames in the feed");
    }

    [Fact]
    public async Task Alarm_photos_from_the_stream_come_from_one_ffmpeg_run()
    {
        hive.Grabber.Reset();
        using var admin = await hive.LoginAsync();
        var nvr = await AddNvrAsync(admin, "Залп", new IpCamChannelRequest(1, "Ворота", RecordWhenDisarmed: true, SnapshotSource: SnapshotSources.Rtsp));
        var cam = nvr.Channels[0].DeviceId;

        _nvr.Motion(0);
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == cam && e.Type == "motion"), "motion");
        var id = await hive.QueryAsync(db => db.Events.Where(e => e.DeviceId == cam && e.Type == "motion").Select(e => e.Id).SingleAsync());
        await hive.WaitForAsync(db => db.Media.CountAsync(m => m.EventId == id).ContinueWith(t => t.Result == 3), "three photos");
        Assert.Single(hive.Grabber.Urls);
    }
}
