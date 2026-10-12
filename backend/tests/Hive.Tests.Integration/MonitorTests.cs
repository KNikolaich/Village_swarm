using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure.Identity;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>The MQTT window for administrators: read-only, admins only, no broker login needed to look.</summary>
[Collection(HiveCollection.Name)]
public sealed class MonitorTests(HiveFixture hive)
{
    [Fact]
    public async Task Admins_see_the_hornets_traffic_and_nobody_else_does()
    {
        var spec = new HornetSpec { Id = "meteo-mon1", Type = "meteo" };
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"));
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        await link.ConnectAsync(spec.Id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "meteo-mon1"), "device seen");

        var admin = await hive.LoginAsync();
        var seen = false;
        for (var i = 0; i < 50 && !seen; i++)
        {
            var page = await admin.GetFromJsonAsync<JsonElement>("/api/monitor");
            seen = page.GetProperty("entries").EnumerateArray()
                .Any(e => e.GetProperty("topic").GetString()!.StartsWith("vs/v1/dev/meteo-mon1/", StringComparison.Ordinal));
            if (!seen) await Task.Delay(100);
        }
        Assert.True(seen);

        // The cursor: asking again with the last sequence returns nothing new.
        var last = (await admin.GetFromJsonAsync<JsonElement>("/api/monitor")).GetProperty("last").GetInt64();
        var later = await admin.GetFromJsonAsync<JsonElement>($"/api/monitor?since={last}");
        Assert.Empty(later.GetProperty("entries").EnumerateArray());

        await hive.CreateUserAsync("viewer-mon", "viewer-mon-password", HiveRoles.Viewer);
        var viewer = await hive.LoginAsync("viewer-mon", "viewer-mon-password");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/monitor")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await hive.App.CreateClient().GetAsync("/api/monitor")).StatusCode);
    }
}
