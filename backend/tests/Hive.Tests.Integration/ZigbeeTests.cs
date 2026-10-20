using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Modules.Devices;
using Hive.Simulator;
using Microsoft.EntityFrameworkCore;
using MQTTnet;

namespace Hive.Tests.Integration;

/// <summary>Zigbee through zigbee2mqtt: the hive gives the container its login, sees what it paired and switches a plug.</summary>
public sealed class ZigbeeTests(DynSecFixture hive) : IClassFixture<DynSecFixture>
{
    private const string Ieee = "0x00158d0001a2b3c4";

    /// <summary>
    /// zigbee2mqtt connects with the login the hive made for it. On a broker that was just set up the api needs about
    /// twenty seconds (its first dynamic security answers are lost until it has its own permissions, then it reconnects),
    /// so the connection is tried again until the login exists and holds (z2m reconnects on its own).
    /// </summary>
    private async Task ConnectBridgeAsync(IMqttClient client, string clientId)
    {
        var why = new List<string>();
        client.DisconnectedAsync += e => { why.Add($"{e.Reason}/{e.ReasonString}/{e.Exception?.Message}"); return Task.CompletedTask; };
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.Broker.Host, hive.Broker.Port)
                    .WithCredentials("zigbee2mqtt", DynSecFixture.ZigbeePassword).WithClientId(clientId).Build());
                await Task.Delay(1500);
                if (client.IsConnected)
                    return;
            }
            catch (Exception ex) when (attempt < 59)
            {
                why.Add("connect: " + ex.Message);
                await Task.Delay(500);
            }
        }
        Assert.Fail("zigbee2mqtt could not stay connected: " + string.Join(" | ", why.Take(6)));
    }

    private static async Task<bool> SeenAsync(List<(string Topic, string Payload)> received, string topic, string payload, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            lock (received)
                if (received.Any(r => r.Topic == topic && r.Payload == payload))
                    return true;
            await Task.Delay(100);
        }
        return false;
    }

    [Fact]
    public async Task Pairing_adding_and_switching_a_plug_end_to_end()
    {
        var admin = hive.App.CreateClient();
        await new SimProvisioner(admin).LoginAsync("admin", HiveFixture.AdminPassword, CancellationToken.None);

        // zigbee2mqtt, with the login the hive made for it
        var received = new List<(string Topic, string Payload)>();
        using var z2m = new MqttClientFactory().CreateMqttClient();
        z2m.ApplicationMessageReceivedAsync += m =>
        {
            lock (received) received.Add((m.ApplicationMessage.Topic, m.ApplicationMessage.ConvertPayloadToString()));
            return Task.CompletedTask;
        };
        await ConnectBridgeAsync(z2m, "z2m-test");
        await z2m.SubscribeAsync("zigbee2mqtt/bridge/request/#");
        await z2m.SubscribeAsync("zigbee2mqtt/+/set");
        await z2m.PublishStringAsync("zigbee2mqtt/bridge/state", """{"state":"online"}""", retain: true);
        var devices = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["ieee_address"] = Ieee, ["friendly_name"] = Ieee, ["type"] = "Router", ["supported"] = true, ["interview_completed"] = true,
            ["definition"] = new System.Text.Json.Nodes.JsonObject { ["model"] = "TS011F", ["vendor"] = "Tuya", ["description"] = "Smart plug" },
        });
        await z2m.PublishStringAsync("zigbee2mqtt/bridge/devices", devices.ToJsonString(), retain: true);

        // the hive sees the bridge and the unpaired device
        ZigbeeDto? seen = null;
        for (var i = 0; i < 100 && seen is not { Devices.Count: 1, Bridge: "online" }; i++)
        {
            seen = await admin.GetFromJsonAsync<ZigbeeDto>("/api/zigbee");
            await Task.Delay(100);
        }
        Assert.True(seen!.Enabled);
        Assert.Equal("online", seen.Bridge);
        Assert.Equal(("TS011F", null), (seen.Devices[0].Model, seen.Devices[0].DeviceId));

        // pairing mode
        using var permit = await admin.PostAsJsonAsync("/api/zigbee/permit-join", new { seconds = 60 });
        Assert.Equal(HttpStatusCode.NoContent, permit.StatusCode);
        Assert.True(await SeenAsync(received, "zigbee2mqtt/bridge/request/permit_join", """{"time":60}"""));
        Assert.NotNull((await admin.GetFromJsonAsync<ZigbeeDto>("/api/zigbee"))!.PermitJoinUntil);

        // adding: z2m is told the new name, the hive makes the hornet
        using var add = await admin.PostAsJsonAsync("/api/zigbee/devices", new { ieee = Ieee, deviceId = "plug-zb1", type = "relay", name = "Розетка в холле" });
        Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);
        Assert.True(await SeenAsync(received, "zigbee2mqtt/bridge/request/device/rename", $$"""{"from":"{{Ieee}}","to":"plug-zb1"}"""));
        var device = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "plug-zb1"));
        Assert.Equal((Drivers.Zigbee, "00158D0001A2B3C4", DeviceStatus.Pending), (device.Driver, device.Mac, device.Status));

        // it reports under its new name
        await z2m.PublishStringAsync("zigbee2mqtt/plug-zb1/availability", """{"state":"online"}""");
        await z2m.PublishStringAsync("zigbee2mqtt/plug-zb1", """{"state":"OFF","power":0,"linkquality":70}""");
        await hive.WaitForAsync(async db =>
        {
            var d = await db.Devices.SingleAsync(x => x.DeviceId == "plug-zb1");
            return d.Status == DeviceStatus.Online && d.State?.RootElement.GetProperty("relay").GetString() == "off";
        }, "zigbee plug online with its state");

        // switching it on goes to its /set, and its report closes the command
        using var sent = await admin.PostAsJsonAsync("/api/devices/plug-zb1/commands", new { name = "relay", payload = new { channel = "relay", set = "on" } });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var cid = (await sent.Content.ReadFromJsonAsync<CommandDto>())!.Cid;
        Assert.True(await SeenAsync(received, "zigbee2mqtt/plug-zb1/set", """{"state":"ON"}"""));
        await z2m.PublishStringAsync("zigbee2mqtt/plug-zb1", """{"state":"ON","power":1480}""");
        await hive.WaitForAsync(async db => (await db.Commands.SingleAsync(x => x.Cid == cid)).Status == CommandStatus.Ok, "command closed by the plug's report");

        // timed on is not offered for Zigbee, and the paired list now knows the device
        using var timed = await admin.PostAsJsonAsync("/api/devices/plug-zb1/commands", new { name = "relay", payload = new { set = "on", for_s = 3600 } });
        Assert.NotEqual(HttpStatusCode.Accepted, timed.StatusCode);
        var known = (await admin.GetFromJsonAsync<ZigbeeDto>("/api/zigbee"))!.Devices.Single();
        Assert.Equal("plug-zb1", known.DeviceId);
    }

    [Fact]
    public async Task A_device_that_is_not_paired_and_a_taken_id_are_refused()
    {
        var admin = hive.App.CreateClient();
        await new SimProvisioner(admin).LoginAsync("admin", HiveFixture.AdminPassword, CancellationToken.None);

        using var missing = await admin.PostAsJsonAsync("/api/zigbee/devices", new { ieee = "0x0000000000000000", deviceId = "plug-zb-none", type = "relay", name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var anonymous = hive.App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/zigbee")).StatusCode);
    }

    [Fact]
    public async Task The_bridge_login_is_limited_to_its_own_topics()
    {
        // the api is up and has made the login once an admin request goes through
        await new SimProvisioner(hive.App.CreateClient()).LoginAsync("admin", HiveFixture.AdminPassword, CancellationToken.None);
        using var z2m = new MqttClientFactory().CreateMqttClient();
        await ConnectBridgeAsync(z2m, "z2m-acl");

        var watcher = new MqttClientFactory().CreateMqttClient();
        var seen = new List<string>();
        watcher.ApplicationMessageReceivedAsync += m =>
        {
            lock (seen) seen.Add($"{m.ApplicationMessage.Topic} {m.ApplicationMessage.ConvertPayloadToString()}");
            return Task.CompletedTask;
        };
        await watcher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.Broker.Host, hive.Broker.Port)
            .WithCredentials("admin", DynSecFixture.AdminPassword).WithClientId("z2m-acl-watcher").Build());
        await watcher.SubscribeAsync("zigbee2mqtt/#");
        await watcher.SubscribeAsync("vs/v1/dev/+/#");
        await watcher.SubscribeAsync("tele/#");

        await z2m.PublishStringAsync("vs/v1/dev/guard-zbacl/status", "online");
        await z2m.PublishStringAsync("tele/heat-zbacl/LWT", "Online");
        await z2m.PublishStringAsync("zigbee2mqtt/probe-zbacl", """{"ok":1}""");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (seen)
                if (seen.Contains("""zigbee2mqtt/probe-zbacl {"ok":1}"""))
                    break;
            await Task.Delay(100);
        }
        lock (seen)
        {
            Assert.Contains("""zigbee2mqtt/probe-zbacl {"ok":1}""", seen);
            Assert.DoesNotContain("vs/v1/dev/guard-zbacl/status online", seen);
            Assert.DoesNotContain("tele/heat-zbacl/LWT Online", seen);
        }
        watcher.Dispose();
    }
}
