using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>The guard banner's three modes: disarmed, armed, and arriving (cameras armed, presence = arriving).</summary>
[Collection(HiveCollection.Name)]
public sealed class GuardModeTests(HiveFixture hive)
{
    [Fact]
    public async Task Arriving_arms_every_camera_and_is_reported_as_its_own_mode()
    {
        const string id = "guard-arrive1";
        var spec = new HornetSpec { Id = id, Type = "guard-cam" };
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"));
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        await link.ConnectAsync(id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == id && d.Status == DeviceStatus.Online), "camera online");
        var admin = await hive.LoginAsync();

        using var arriving = await admin.PutAsJsonAsync("/api/modes/armed", new { armed = true, mode = "arriving" });
        Assert.Equal(HttpStatusCode.OK, arriving.StatusCode);
        var state = await arriving.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("arriving", state.GetProperty("mode").GetString());
        Assert.True(state.GetProperty("armed").GetBoolean());
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.DeviceId == id && c.Name == "arm" && c.IssuedBy == "user:admin"), "arm sent to the camera");
        await hive.WaitForAsync(db => db.Modes.AnyAsync(m => m.Key == Mode.Presence && m.Value == Mode.PresenceArriving), "presence arriving");

        using var unknown = await admin.PutAsJsonAsync("/api/modes/armed", new { armed = true, mode = "party" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // Old clients send only the flag.
        using var off = await admin.PutAsJsonAsync("/api/modes/armed", new { armed = false });
        var after = await off.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("disarmed", after.GetProperty("mode").GetString());
        Assert.Equal("disarmed", (await admin.GetFromJsonAsync<JsonElement>("/api/modes/armed")).GetProperty("mode").GetString());
    }
}
