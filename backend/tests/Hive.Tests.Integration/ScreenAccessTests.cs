using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>A monitor reads its screen from anywhere with its own token: read only, revocable (contracts/monitor-http.md).</summary>
[Collection(HiveCollection.Name)]
public sealed class ScreenAccessTests(HiveFixture hive)
{
    private async Task RegisterAsync(string id)
    {
        var spec = new HornetSpec { Id = id, Type = "meteo" };
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"));
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        await link.ConnectAsync(id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == id && d.Status == DeviceStatus.Online), "device online");
    }

    private HttpRequestMessage Frame(string? token, string? etag = null, bool external = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/monitor/v1/frame");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (etag is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (external)
            request.Headers.Add("X-Hive-External", "1");
        return request;
    }

    [Fact]
    public async Task A_monitor_reads_its_screen_with_its_token_until_the_token_is_revoked()
    {
        await RegisterAsync("monitor-far1");
        await RegisterAsync("meteo-far-src");
        await hive.QueryAsync(async db =>
        {
            // the simulator has no monitor role: the hive is told the type the monitor firmware would report
            await db.Database.ExecuteSqlRawAsync("UPDATE devices SET type_code = 'monitor' WHERE device_id = 'monitor-far1'");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO telemetry (device_id, metric, ts, value, received_at)
                VALUES ('meteo-far-src', 'temperature', now(), 21.54, now())
                """);
            return 0;
        });
        var admin = await hive.LoginAsync();
        var anonymous = hive.App.CreateClient();

        // only monitors get a token
        using var notMonitor = await admin.PostAsync("/api/devices/meteo-far-src/screen-access", null);
        Assert.Equal(HttpStatusCode.BadRequest, notMonitor.StatusCode);

        using var display = await admin.PutAsJsonAsync("/api/devices/monitor-far1/display",
            new { items = new object[] { new { device = "meteo-far-src", metric = "temperature", label = "Дача", x = 0, y = 0, size = 6 } } });
        Assert.Equal(HttpStatusCode.OK, display.StatusCode);

        using var issued = await admin.PostAsync("/api/devices/monitor-far1/screen-access", null);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var token = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        // short enough to type: 5 groups of 4 letters and digits that look different
        Assert.Matches("^[2-9a-hjkmnp-z]{4}(-[2-9a-hjkmnp-z]{4}){4}$", token);
        var stored = await hive.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == "monitor-far1"));
        Assert.NotEqual(token, stored.ScreenTokenHash);  // only the hash is kept

        using (var none = await anonymous.SendAsync(Frame(null)))
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        using (var wrong = await anonymous.SendAsync(Frame("not-a-token")))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // as it is read off a screen or typed on a phone: no dashes, any case
        using (var typed = await anonymous.SendAsync(Frame(token.Replace("-", "").ToUpperInvariant())))
            Assert.Equal(HttpStatusCode.OK, typed.StatusCode);
        using (var wrongOne = await anonymous.SendAsync(Frame(token[..^1] + (token[^1] == 'z' ? 'y' : 'z'))))
            Assert.Equal(HttpStatusCode.Unauthorized, wrongOne.StatusCode);

        using var frame = await anonymous.SendAsync(Frame(token, external: true));
        Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
        Assert.Equal("no-store", frame.Headers.CacheControl?.ToString());
        var etag = frame.Headers.ETag!.Tag;
        var item = (await frame.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
        Assert.Equal(("Дача", "21.5°", 6), (item.GetProperty("label").GetString(), item.GetProperty("text").GetString(), item.GetProperty("size").GetInt32()));

        // nothing changed: a sleeping panel gets 304 and no body
        using (var same = await anonymous.SendAsync(Frame(token, etag, external: true)))
            Assert.Equal(HttpStatusCode.NotModified, same.StatusCode);

        var access = await admin.GetFromJsonAsync<JsonElement>("/api/devices/monitor-far1/screen-access");
        Assert.True(access.GetProperty("enabled").GetBoolean());
        Assert.True(access.GetProperty("lastReadExternal").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, access.GetProperty("lastReadAt").ValueKind);

        // a new token replaces the old one at once; revoking closes it
        using var again = await admin.PostAsync("/api/devices/monitor-far1/screen-access", null);
        var second = (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        using (var old = await anonymous.SendAsync(Frame(token)))
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using (var fresh = await anonymous.SendAsync(Frame(second)))
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        using var revoked = await admin.DeleteAsync("/api/devices/monitor-far1/screen-access");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using (var closed = await anonymous.SendAsync(Frame(second)))
            Assert.Equal(HttpStatusCode.Unauthorized, closed.StatusCode);
    }

    private HttpRequestMessage Bitmap(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/monitor/v1/frame.bin");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Hive-External", "1");
        return request;
    }

    [Fact]
    public async Task A_monitor_that_never_read_can_be_removed_for_good_and_a_seen_one_cannot()
    {
        var admin = await hive.LoginAsync();
        using var added = await admin.PostAsJsonAsync("/api/devices/remote-monitors", new { deviceId = "monitor-lost1", name = "Неудачник", board = "waveshare-s3-epaper-154" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);

        using var purged = await admin.DeleteAsync("/api/devices/monitor-lost1?purge=true");
        Assert.Equal(HttpStatusCode.NoContent, purged.StatusCode);
        Assert.False(await hive.QueryAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "monitor-lost1")));
        using var again = await admin.DeleteAsync("/api/devices/monitor-lost1?purge=true");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        // the id is free again
        using var reused = await admin.PostAsJsonAsync("/api/devices/remote-monitors", new { deviceId = "monitor-lost1", name = "Второй", board = "waveshare-s3-epaper-154" });
        Assert.Equal(HttpStatusCode.Created, reused.StatusCode);

        // one the hive has seen keeps its history: only the archive
        using var token = await admin.PostAsync("/api/devices/monitor-lost1/screen-access", null);
        var secret = (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        using (var read = await hive.App.CreateClient().SendAsync(Bitmap(secret)))
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var refused = await admin.DeleteAsync("/api/devices/monitor-lost1?purge=true");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.True(await hive.QueryAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "monitor-lost1")));
    }

    [Fact]
    public async Task A_monitor_far_away_is_added_by_hand_and_reads_the_picture_the_hive_draws()
    {
        var admin = await hive.LoginAsync();
        var anonymous = hive.App.CreateClient();

        using var badBoard = await admin.PostAsJsonAsync("/api/devices/remote-monitors", new { deviceId = "monitor-city1", name = "Город", board = "esp32c3-oled-042" });
        Assert.Equal(HttpStatusCode.BadRequest, badBoard.StatusCode);  // the hive does not draw that screen
        using var added = await admin.PostAsJsonAsync("/api/devices/remote-monitors", new { deviceId = "monitor-city1", name = "Город", board = "waveshare-s3-epaper-154" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        using var twice = await admin.PostAsJsonAsync("/api/devices/remote-monitors", new { deviceId = "monitor-city1", name = "Город", board = "waveshare-s3-epaper-154" });
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);

        using var display = await admin.PutAsJsonAsync("/api/devices/monitor-city1/display",
            new { items = new object[] { new { device = "meteo-far-src", metric = "temperature", x = 10, y = 60, size = 4 } } });
        Assert.Equal(HttpStatusCode.OK, display.StatusCode);
        var screen = (await admin.GetFromJsonAsync<JsonElement>("/api/devices/monitor-city1/display")).GetProperty("screen");
        Assert.Equal((200, 200, true), (screen.GetProperty("width").GetInt32(), screen.GetProperty("height").GetInt32(), screen.GetProperty("rendered").GetBoolean()));

        using var preview = await admin.GetAsync("/api/devices/monitor-city1/display/preview.png");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("image/png", preview.Content.Headers.ContentType?.MediaType);

        using var issued = await admin.PostAsync("/api/devices/monitor-city1/screen-access", null);
        var token = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        using var frame = await anonymous.SendAsync(Bitmap(token));
        Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
        Assert.Equal("200", frame.Headers.GetValues("X-Frame-Width").Single());
        var bits = await frame.Content.ReadAsByteArrayAsync();
        Assert.Equal(5000, bits.Length);
        Assert.Contains(bits, b => b != 0);  // something is drawn

        // seen by its read: online, its last read noted
        var device = await hive.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == "monitor-city1"));
        Assert.Equal((DeviceStatus.Online, "http"), (device.Status, device.Driver));
        Assert.NotNull(device.LastSeenAt);

        // no MQTT there: no updates over the air either
        using var ota = await admin.PostAsJsonAsync("/api/devices/monitor-city1/ota", new { build = "monitor-epd154" });
        Assert.Equal(HttpStatusCode.BadRequest, ota.StatusCode);

        // the small OLED draws its own: no picture from the hive for it
        await RegisterAsync("monitor-oled-b");
        await hive.QueryAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE devices SET type_code = 'monitor', hw = 'esp32c3-oled-042' WHERE device_id = 'monitor-oled-b'");
            return 0;
        });
        using var oledToken = await admin.PostAsync("/api/devices/monitor-oled-b/screen-access", null);
        var oled = (await oledToken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        using (var none = await anonymous.SendAsync(Bitmap(oled)))
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }
}
