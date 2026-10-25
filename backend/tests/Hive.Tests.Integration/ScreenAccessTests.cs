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
        Assert.True(token.Length >= 40);
        var stored = await hive.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == "monitor-far1"));
        Assert.NotEqual(token, stored.ScreenTokenHash);  // only the hash is kept

        using (var none = await anonymous.SendAsync(Frame(null)))
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        using (var wrong = await anonymous.SendAsync(Frame("not-a-token")))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

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
}
