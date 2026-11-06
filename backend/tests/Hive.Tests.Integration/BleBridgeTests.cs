using System.Net;
using System.Net.Http.Json;
using Hive.Domain;
using Hive.Modules.Devices;
using Microsoft.EntityFrameworkCore;
using MQTTnet;

namespace Hive.Tests.Integration;

/// <summary>A Bluetooth bridge (type ble): the thermometer's address is set from the hive and goes out as the retained config.</summary>
[Collection(HiveCollection.Name)]
public sealed class BleBridgeTests(HiveFixture hive)
{
    private Task AddBridgeAsync(string id, string type = "ble") => hive.QueryAsync(async db =>
    {
        db.Devices.Add(new Device { DeviceId = id, TypeCode = type, Name = "Кухонный термометр", Status = DeviceStatus.Online });
        await db.SaveChangesAsync();
        return 0;
    });

    private async Task<string> RetainedConfigAsync(string id, int rev)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        string? payload = null;
        client.ApplicationMessageReceivedAsync += m =>
        {
            var text = m.ApplicationMessage.ConvertPayloadToString();
            if (text.Contains($"\"rev\":{rev}", StringComparison.Ordinal))
                payload = text;
            return Task.CompletedTask;
        };
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.MqttHost, hive.MqttPort).WithClientId($"ble-{id}").Build());
        await client.SubscribeAsync($"vs/v1/dev/{id}/config");
        for (var i = 0; i < 50 && payload is null; i++)
            await Task.Delay(100);
        return payload ?? throw new TimeoutException($"retained config rev {rev} for {id}");
    }

    [Fact]
    public async Task The_thermometer_address_goes_into_the_retained_config_and_can_be_cleared()
    {
        await AddBridgeAsync("ble-kitchen1");
        var admin = await hive.LoginAsync();

        using var saved = await admin.PutAsJsonAsync("/api/devices/ble-kitchen1/ble", new { mac = "aa:bb:cc:dd:ee:ff", dump = true });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var now = await admin.GetFromJsonAsync<DeviceConfigService.BleRequest>("/api/devices/ble-kitchen1/ble");
        Assert.Equal(("AA:BB:CC:DD:EE:FF", true), (now!.Mac, now.Dump));
        var config = await RetainedConfigAsync("ble-kitchen1", 1);
        Assert.Contains("\"mac\":\"AA:BB:CC:DD:EE:FF\"", config);
        Assert.Contains("\"dump\":true", config);

        using var cleared = await admin.PutAsJsonAsync("/api/devices/ble-kitchen1/ble", new { });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await admin.GetFromJsonAsync<DeviceConfigService.BleRequest>("/api/devices/ble-kitchen1/ble"))!.Mac);
        Assert.DoesNotContain("\"ble\"", await RetainedConfigAsync("ble-kitchen1", 2));
    }

    [Fact]
    public async Task A_wrong_address_is_refused_and_only_bridges_have_one()
    {
        await AddBridgeAsync("ble-kitchen2");
        await AddBridgeAsync("meteo-not-ble", "meteo");
        var admin = await hive.LoginAsync();

        using var bad = await admin.PutAsJsonAsync("/api/devices/ble-kitchen2/ble", new { mac = "not-a-mac" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var other = await admin.PutAsJsonAsync("/api/devices/meteo-not-ble/ble", new { mac = "AA:BB:CC:DD:EE:FF" });
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/devices/meteo-not-ble/ble")).StatusCode);
    }
}
