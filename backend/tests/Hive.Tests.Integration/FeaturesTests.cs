using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>What a hornet is used for (config "features"): set from the UI, checked against what its firmware can do.</summary>
[Collection(HiveCollection.Name)]
public sealed class FeaturesTests(HiveFixture hive)
{
    private async Task RegisterAsync(string id, string type)
    {
        var spec = new HornetSpec { Id = id, Type = type };
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"));
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        await link.ConnectAsync(id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == id && d.Status == DeviceStatus.Online), "device online");
    }

    private static string[] Features(JsonElement dto) =>
        dto.GetProperty("features").EnumerateArray().Select(f => f.GetString()!).ToArray();

    [Fact]
    public async Task A_heat_hornet_can_be_a_thermometer_or_a_heater_with_its_limits()
    {
        await RegisterAsync("heat-feat1", "heat");
        var admin = await hive.LoginAsync();

        var before = await admin.GetFromJsonAsync<JsonElement>("/api/devices/heat-feat1");
        Assert.Equal(["thermometer", "relay", "heater"], Features(before));  // the role's default
        Assert.Equal(25, before.GetProperty("heater").GetProperty("maxC").GetDouble());

        using var thermometer = await admin.PutAsJsonAsync("/api/devices/heat-feat1/features", new { features = new[] { "thermometer" } });
        Assert.Equal(HttpStatusCode.OK, thermometer.StatusCode);
        var dto = await thermometer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["thermometer"], Features(dto));
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("heater").ValueKind);

        using var heater = await admin.PutAsJsonAsync("/api/devices/heat-feat1/features",
            new { features = new[] { "thermometer", "heater" }, minC = 8.0, maxC = 22.0, maxOnS = 3600 });
        Assert.Equal(HttpStatusCode.OK, heater.StatusCode);
        Assert.Equal(["thermometer", "relay", "heater"], Features(await heater.Content.ReadFromJsonAsync<JsonElement>()));
        var device = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "heat-feat1"));
        var config = device.Config!.RootElement;
        Assert.Equal(["thermometer", "relay", "heater"], config.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        var limits = config.GetProperty("failsafe").GetProperty("heater");
        Assert.Equal((8.0, 22.0, 3600), (limits.GetProperty("min_c").GetDouble(), limits.GetProperty("max_c").GetDouble(), limits.GetProperty("max_on_s").GetInt32()));
    }

    [Fact]
    public async Task Features_the_firmware_cannot_do_are_refused()
    {
        await RegisterAsync("heat-feat2", "heat");
        await RegisterAsync("meteo-feat1", "meteo");
        var admin = await hive.LoginAsync();

        using var noSensor = await admin.PutAsJsonAsync("/api/devices/heat-feat2/features", new { features = new[] { "heater" } });
        Assert.Equal(HttpStatusCode.BadRequest, noSensor.StatusCode);
        using var badLimits = await admin.PutAsJsonAsync("/api/devices/heat-feat2/features", new { features = new[] { "thermometer", "heater" }, minC = 20.0, maxC = 20.5 });
        Assert.Equal(HttpStatusCode.BadRequest, badLimits.StatusCode);
        using var meteoRelay = await admin.PutAsJsonAsync("/api/devices/meteo-feat1/features", new { features = new[] { "relay" } });
        Assert.Equal(HttpStatusCode.BadRequest, meteoRelay.StatusCode);
        using var unknown = await admin.PutAsJsonAsync("/api/devices/nobody-here/features", new { features = new[] { "thermometer" } });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
