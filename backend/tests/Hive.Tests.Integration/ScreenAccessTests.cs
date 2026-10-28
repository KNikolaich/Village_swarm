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

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(SkiaSharp.SKColors.Black);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        return image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public async Task A_background_of_the_right_size_a_turn_and_a_caption_change_the_frame_a_panel_reads()
    {
        var admin = await hive.LoginAsync();
        using var added = await admin.PostAsJsonAsync("/api/devices/remote-monitors", new { deviceId = "monitor-bg1", name = "Фон", board = "waveshare-s3-epaper-154" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        using var issued = await admin.PostAsync("/api/devices/monitor-bg1/screen-access", null);
        var token = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var anonymous = hive.App.CreateClient();

        async Task<(string ETag, byte[] Body)> Read()
        {
            using var response = await anonymous.SendAsync(Bitmap(token));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (response.Headers.ETag!.Tag, await response.Content.ReadAsByteArrayAsync());
        }
        var blank = await Read();
        Assert.Equal(200 * 25, blank.Body.Length);  // 200 pixels of 200 rows, one bit each

        // a caption in a font of the hive, laid out by hand
        using var saved = await admin.PutAsJsonAsync("/api/devices/monitor-bg1/display", new
        {
            items = new object[] { new { metric = "", text = "Дача", x = 4, y = 4, size = 2, font = "anonymous-pro", px = 24, bold = true } },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var captioned = await Read();
        Assert.NotEqual(blank.ETag, captioned.ETag);
        Assert.Contains(captioned.Body, b => b != 0);  // a set bit is black

        // a family id is a slug; one that is not in the catalog is only drawn in the standard font
        using var badFont = await admin.PutAsJsonAsync("/api/devices/monitor-bg1/display",
            new { items = new object[] { new { metric = "", text = "x", x = 0, y = 0, size = 2, font = "Bad Font" } } });
        Assert.Equal(HttpStatusCode.BadRequest, badFont.StatusCode);

        // the background: exactly the size of the screen
        using var wrongSize = await admin.PutAsync("/api/devices/monitor-bg1/display/background", new ByteArrayContent(Png(100, 100)));
        Assert.Equal(HttpStatusCode.BadRequest, wrongSize.StatusCode);
        using var notImage = await admin.PutAsync("/api/devices/monitor-bg1/display/background", new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.BadRequest, notImage.StatusCode);
        using var right = await admin.PutAsync("/api/devices/monitor-bg1/display/background", new ByteArrayContent(Png(200, 200)));
        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        var withBackground = await Read();
        Assert.NotEqual(captioned.ETag, withBackground.ETag);
        Assert.True(withBackground.Body.Count(b => b == 0xFF) > 4000, "the black background is under the caption");
        using var shown = await admin.GetAsync("/api/devices/monitor-bg1/display/background.png");
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

        // turned a quarter, a square panel keeps its size and the picture is drawn turned: the tag changes, so the panel redraws
        using var turned = await admin.PutAsJsonAsync("/api/devices/monitor-bg1/display", new { items = Array.Empty<object>(), rotation = 90 });
        Assert.Equal(HttpStatusCode.OK, turned.StatusCode);
        var rotated = await Read();
        Assert.NotEqual(withBackground.ETag, rotated.ETag);
        using var badTurn = await admin.PutAsJsonAsync("/api/devices/monitor-bg1/display", new { items = Array.Empty<object>(), rotation = 45 });
        Assert.Equal(HttpStatusCode.BadRequest, badTurn.StatusCode);

        // inverted, the screen is white on black: the tag changes and so does every bit; saving without it keeps it
        using var dark = await admin.PutAsJsonAsync("/api/devices/monitor-bg1/display", new { items = Array.Empty<object>(), invert = true });
        Assert.Equal(HttpStatusCode.OK, dark.StatusCode);
        Assert.True((await dark.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invert").GetBoolean());
        var inverted = await Read();
        Assert.NotEqual(rotated.ETag, inverted.ETag);
        Assert.Equal(rotated.Body.Select(b => (byte)~b), inverted.Body);
        using var kept = await admin.PutAsJsonAsync("/api/devices/monitor-bg1/display", new { items = Array.Empty<object>() });
        Assert.Equal(inverted.ETag, (await Read()).ETag);
        using var light = await admin.PutAsJsonAsync("/api/devices/monitor-bg1/display", new { items = Array.Empty<object>(), invert = false });
        Assert.Equal(rotated.ETag, (await Read()).ETag);

        using var removed = await admin.DeleteAsync("/api/devices/monitor-bg1/display/background");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var gone = await admin.GetAsync("/api/devices/monitor-bg1/display/background.png");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Fonts_are_listed_with_their_marks_and_a_person_can_upload_and_remove_their_own()
    {
        var admin = await hive.LoginAsync();
        var fonts = await admin.GetFromJsonAsync<JsonElement>("/api/display/fonts");
        var list = fonts.EnumerateArray().ToList();
        Assert.Contains(list, f => f.GetProperty("id").GetString() == "standard" && f.GetProperty("cyrillic").GetBoolean());
        var digits = list.Single(f => f.GetProperty("id").GetString() == "dseg7");
        Assert.True(digits.GetProperty("digitsOnly").GetBoolean());
        Assert.False(digits.GetProperty("cyrillic").GetBoolean());

        using var rubbish = await admin.PostAsync("/api/display/fonts", new ByteArrayContent([1, 2, 3, 4]));
        Assert.Equal(HttpStatusCode.BadRequest, rubbish.StatusCode);

        var file = typeof(Hive.Modules.Devices.DisplayFontCatalog).Assembly.GetManifestResourceStream("Hive.Modules.Devices.Fonts.AnonymousPro-Regular.ttf")!;
        using var bytes = new MemoryStream();
        await file.CopyToAsync(bytes);
        using var uploaded = await admin.PostAsync("/api/display/fonts", new ByteArrayContent(bytes.ToArray()));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var id = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.Contains((await admin.GetFromJsonAsync<JsonElement>("/api/display/fonts")).EnumerateArray(), f => f.GetProperty("id").GetString() == id && !f.GetProperty("builtin").GetBoolean());
        using var removed = await admin.DeleteAsync($"/api/display/fonts/{id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var again = await admin.DeleteAsync($"/api/display/fonts/{id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
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
    public async Task A_panel_with_tabs_reads_each_tab_and_tells_its_battery()
    {
        var admin = await hive.LoginAsync();
        var anonymous = hive.App.CreateClient();
        using var added = await admin.PostAsJsonAsync("/api/devices/remote-monitors", new { deviceId = "monitor-crow1", name = "Кухня", board = "crowpanel-epd37" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        // landscape unless turned: the person lays out 416x240
        var screen = (await admin.GetFromJsonAsync<JsonElement>("/api/devices/monitor-crow1/display")).GetProperty("screen");
        Assert.Equal((416, 240, 3), (screen.GetProperty("width").GetInt32(), screen.GetProperty("height").GetInt32(), screen.GetProperty("tabs").GetInt32()));
        using var issued = await admin.PostAsync("/api/devices/monitor-crow1/screen-access", null);
        var token = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        HttpRequestMessage Tab(int tab)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/monitor/v1/frame.bin?tab={tab}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Battery-mV", "3912");
            return request;
        }
        var etags = new HashSet<string>();
        for (var tab = 0; tab < 3; tab++)
        {
            using var frame = await anonymous.SendAsync(Tab(tab));
            Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
            Assert.False(frame.Headers.Contains("X-Frame-Refresh"));  // not monitored: the panel keeps its own interval
            // the panel's own size (portrait), turned by the hive
            Assert.Equal(("240", "416", "3", "270"), (frame.Headers.GetValues("X-Frame-Width").Single(), frame.Headers.GetValues("X-Frame-Height").Single(),
                frame.Headers.GetValues("X-Frame-Tabs").Single(), frame.Headers.GetValues("X-Frame-Rotation").Single()));
            Assert.Equal("2,3,3", frame.Headers.GetValues("X-Frame-Tab-Weights").Single());  // the cells of the tab bar
            Assert.Equal(30 * 416, (await frame.Content.ReadAsByteArrayAsync()).Length);
            etags.Add(frame.Headers.ETag!.Tag);
        }
        Assert.Equal(3, etags.Count);  // each tab has its own picture
        using (var none = await anonymous.SendAsync(Tab(3)))
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        // sleep (a long press of EXIT): without a picture the tab it showed, with its own picture; with one, that picture
        HttpRequestMessage Sleeping()
        {
            var request = Tab(1);
            request.RequestUri = new Uri("/api/monitor/v1/frame.bin?tab=1&sleep=1", UriKind.Relative);
            return request;
        }
        using (var plain = await anonymous.SendAsync(Sleeping()))
        {
            Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
            Assert.StartsWith("\"s", plain.Headers.ETag!.Tag);
            Assert.DoesNotContain(plain.Headers.ETag!.Tag, etags);
            Assert.Equal(30 * 416, (await plain.Content.ReadAsByteArrayAsync()).Length);
        }
        using (var tooBig = await admin.PutAsync("/api/devices/monitor-crow1/display/sleep", new ByteArrayContent(Png(300, 100))))
            Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);  // the screen is 416x240 as it is laid out
        using (var small = await admin.PutAsync("/api/devices/monitor-crow1/display/sleep", new ByteArrayContent(Png(100, 60))))
            Assert.Equal(HttpStatusCode.NoContent, small.StatusCode);
        using (var asleep = await anonymous.SendAsync(Sleeping()))
        {
            var bits = await asleep.Content.ReadAsByteArrayAsync();
            Assert.Equal(30 * 416, bits.Length);
            // a 1 is a black pixel: the 100x60 picture, turned, is some 700 full bytes and the rest of the screen is white
            Assert.InRange(bits.Count(b => b == 0xFF), 300, 1000);
            Assert.True(bits.Count(b => b == 0x00) > bits.Length / 2, "white is around it");
        }
        using (var shown = await admin.GetAsync("/api/devices/monitor-crow1/display/sleep.png"))
            Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        using (var removed = await admin.DeleteAsync("/api/devices/monitor-crow1/display/sleep"))
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        // monitoring: the panel is told to read every 2 minutes for an hour, then nothing again
        using (var wrong = await admin.PutAsJsonAsync("/api/devices/monitor-crow1/display/monitoring", new { hours = 1, everyMin = 3 }))
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using (var on = await admin.PutAsJsonAsync("/api/devices/monitor-crow1/display/monitoring", new { hours = 1, everyMin = 2 }))
        {
            Assert.Equal(HttpStatusCode.OK, on.StatusCode);
            Assert.Equal(2, (await on.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("monitor").GetProperty("everyMin").GetInt32());
        }
        using (var monitored = await anonymous.SendAsync(Tab(1)))
            Assert.Equal("120", monitored.Headers.GetValues("X-Frame-Refresh").Single());
        // a clock on the Measure tab keeps the monitoring (the person's layout and the monitoring live side by side)
        using (var clock = await admin.PutAsJsonAsync("/api/devices/monitor-crow1/display",
            new { items = new object[] { new { metric = "", clock = "time", x = 10, y = 20, size = 5, px = 96 }, new { metric = "", clock = "date", format = "d MMMM", x = 10, y = 150, size = 2, px = 24 } } }))
            Assert.Equal(HttpStatusCode.OK, clock.StatusCode);
        var display = await admin.GetFromJsonAsync<JsonElement>("/api/devices/monitor-crow1/display");
        Assert.Equal("time", display.GetProperty("items")[0].GetProperty("clock").GetString());
        Assert.Equal("d MMMM", display.GetProperty("items")[1].GetProperty("format").GetString());
        Assert.Equal(JsonValueKind.Object, display.GetProperty("monitor").ValueKind);
        using (var off = await admin.DeleteAsync("/api/devices/monitor-crow1/display/monitoring"))
            Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        using (var free = await anonymous.SendAsync(Tab(1)))
            Assert.False(free.Headers.Contains("X-Frame-Refresh"));
        using var preview = await admin.GetAsync("/api/devices/monitor-crow1/display/preview.png?tab=1");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

        // the battery it told is its reading
        await hive.WaitForAsync(db => db.Telemetry.AnyAsync(t => t.DeviceId == "monitor-crow1" && t.Metric == "battery_voltage" && t.Value > 3.9 && t.Value < 3.92), "battery noted");
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
