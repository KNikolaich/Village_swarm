using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Modules.Devices;
using Hive.Simulator;
using Microsoft.EntityFrameworkCore;
using MQTTnet;

namespace Hive.Tests.Integration;

/// <summary>A Tasmota or OpenBeken device on a broker with per-device logins, from the setup lines to a switched relay.</summary>
public sealed class TasmotaTests(DynSecFixture hive) : IClassFixture<DynSecFixture>
{
    private async Task<HttpClient> AdminClientAsync()
    {
        var client = hive.App.CreateClient();
        await new SimProvisioner(client).LoginAsync("admin", HiveFixture.AdminPassword, CancellationToken.None);
        return client;
    }

    private static async Task<TasmotaSetup> AddAsync(HttpClient admin, string id, string type = "heat")
    {
        using var response = await admin.PostAsJsonAsync("/api/provision/tasmota", new { deviceId = id, type, name = "Обогрев спальни" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TasmotaSetup>())!;
    }

    /// <summary>What the device does: connects with the login from the setup lines and listens to its cmnd topic.</summary>
    private async Task<(IMqttClient Client, List<(string Topic, string Payload)> Received)> ConnectAsync(TasmotaSetup setup)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        var received = new List<(string, string)>();
        client.ApplicationMessageReceivedAsync += m =>
        {
            lock (received) received.Add((m.ApplicationMessage.Topic, m.ApplicationMessage.ConvertPayloadToString()));
            return Task.CompletedTask;
        };
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.Broker.Host, hive.Broker.Port)
            .WithCredentials(setup.Mqtt.User, setup.Mqtt.Pass).WithClientId(setup.DeviceId).Build());
        await client.SubscribeAsync($"cmnd/{setup.DeviceId}/#");
        return (client, received);
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
    public async Task Setup_lines_point_the_device_at_the_hive_with_its_own_login()
    {
        var admin = await AdminClientAsync();
        var setup = await AddAsync(admin, "heat-tas-setup");

        Assert.Equal("heat-tas-setup", setup.Mqtt.User);
        Assert.False(string.IsNullOrEmpty(setup.Mqtt.Pass));
        var lines = string.Join('\n', setup.Lines);
        Assert.Contains("Topic heat-tas-setup", lines);
        Assert.Contains($"MqttUser heat-tas-setup; MqttPassword {setup.Mqtt.Pass}", lines);
        Assert.Contains("PowerOnState 0", lines);
        Assert.Contains("PulseTime1 21700", lines);   // a heater switches itself off after 6 hours at most

        var device = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "heat-tas-setup"));
        Assert.Equal((Drivers.Tasmota, DeviceStatus.Pending), (device.Driver, device.Status));

        // the password is shown once: the database keeps nothing of it
        Assert.DoesNotContain(setup.Mqtt.Pass!, JsonSerializer.Serialize(device.Info));
    }

    [Fact]
    public async Task Reports_become_state_and_telemetry_and_a_command_reaches_the_relay()
    {
        var admin = await AdminClientAsync();
        var setup = await AddAsync(admin, "heat-tas-live");
        var (device, received) = await ConnectAsync(setup);
        using var _ = device;

        await device.PublishStringAsync("tele/heat-tas-live/LWT", "Online", retain: true);
        await device.PublishStringAsync("tele/heat-tas-live/STATE", """{"UptimeSec":60,"Heap":26,"MqttCount":1,"POWER":"OFF","Wifi":{"Signal":-61}}""");
        await device.PublishStringAsync("tele/heat-tas-live/SENSOR", """{"AM2301":{"Temperature":18.5,"Humidity":44}}""");
        await hive.WaitForAsync(async db =>
        {
            var d = await db.Devices.SingleAsync(x => x.DeviceId == "heat-tas-live");
            return d.Status == DeviceStatus.Online && d.Rssi == -61 && d.State?.RootElement.GetProperty("relay").GetString() == "off";
        }, "tasmota device online with its state");

        var latest = await admin.GetFromJsonAsync<JsonElement>("/api/telemetry/latest?device=heat-tas-live");
        Assert.Contains("18.5", latest.GetRawText());

        // a timed switch-on goes out as a Tasmota command, and the device's answer closes it
        using var sent = await admin.PostAsJsonAsync("/api/devices/heat-tas-live/commands",
            new { name = "relay", payload = new { channel = "heater", set = "on", for_s = 3600 } });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var cid = (await sent.Content.ReadFromJsonAsync<CommandDto>())!.Cid;
        Assert.True(await SeenAsync(received, "cmnd/heat-tas-live/Backlog", "PulseTime1 3700; POWER ON"), "command reached the device");

        await device.PublishStringAsync("stat/heat-tas-live/POWER", "ON");
        await hive.WaitForAsync(async db =>
        {
            var c = await db.Commands.SingleAsync(x => x.Cid == cid);
            var d = await db.Devices.SingleAsync(x => x.DeviceId == "heat-tas-live");
            return c.Status == CommandStatus.Ok && d.State?.RootElement.GetProperty("relay").GetString() == "on";
        }, "command acknowledged by the device's answer");
    }

    [Fact]
    public async Task Commands_a_tasmota_device_cannot_do_are_refused()
    {
        var admin = await AdminClientAsync();
        await AddAsync(admin, "heat-tas-refuse");
        using var response = await admin.PostAsJsonAsync("/api/devices/heat-tas-refuse/commands", new { name = "snapshot", payload = new { } });
        Assert.NotEqual(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task A_device_can_only_speak_and_listen_on_its_own_topics()
    {
        var admin = await AdminClientAsync();
        var a = await AddAsync(admin, "heat-tas-a");
        await AddAsync(admin, "heat-tas-b");
        var (device, _) = await ConnectAsync(a);
        using var _ = device;

        var watcher = new MqttClientFactory().CreateMqttClient();
        var seen = new List<string>();
        watcher.ApplicationMessageReceivedAsync += m =>
        {
            lock (seen) seen.Add($"{m.ApplicationMessage.Topic} {m.ApplicationMessage.ConvertPayloadToString()}");
            return Task.CompletedTask;
        };
        await watcher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.Broker.Host, hive.Broker.Port)
            .WithCredentials("admin", DynSecFixture.AdminPassword).WithClientId("tasmota-acl-watcher").Build());
        await watcher.SubscribeAsync("tele/#");
        await watcher.SubscribeAsync("vs/v1/dev/+/#");

        // pretending to be B, and writing into our own hornet contract
        await device.PublishStringAsync("tele/heat-tas-b/LWT", "Offline");
        await device.PublishStringAsync("vs/v1/dev/heat-tas-a/status", "online");
        await device.PublishStringAsync("tele/heat-tas-a/LWT", "Online");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (seen)
                if (seen.Contains("tele/heat-tas-a/LWT Online"))
                    break;
            await Task.Delay(100);
        }
        lock (seen)
        {
            Assert.Contains("tele/heat-tas-a/LWT Online", seen);
            Assert.DoesNotContain("tele/heat-tas-b/LWT Offline", seen);
            Assert.DoesNotContain("vs/v1/dev/heat-tas-a/status online", seen);
        }
        watcher.Dispose();
    }

    [Fact]
    public async Task An_id_of_our_own_hornet_cannot_be_taken_over_and_a_removed_one_can_be_reused()
    {
        var admin = await AdminClientAsync();
        var hornet = await admin.PostAsJsonAsync("/api/provision/codes", new { deviceId = "guard-tas-own", type = "guard-cam", name = "Камера" });
        var code = (await hornet.Content.ReadFromJsonAsync<EnrollmentCodeDto>())!;
        using var enroll = await hive.App.CreateClient().PostAsJsonAsync("/api/provision/enroll", new { code = code.Code, mac = "02:00:00:00:0a:01" });
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);

        using var taken = await admin.PostAsJsonAsync("/api/provision/tasmota", new { deviceId = "guard-tas-own", type = "relay", name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, taken.StatusCode);

        // asking again for a Tasmota device gives it a new login: the old password stops working
        var first = await AddAsync(admin, "relay-tas-again", "relay");
        var second = await AddAsync(admin, "relay-tas-again", "relay");
        Assert.NotEqual(first.Mqtt.Pass, second.Mqtt.Pass);
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(first));
        var (ok, _) = await ConnectAsync(second);
        ok.Dispose();
    }

    [Fact]
    public async Task A_console_command_reaches_the_device_and_its_answer_lands_in_the_log()
    {
        var admin = await AdminClientAsync();
        var setup = await AddAsync(admin, "rf-tas-console", "relay");
        var (device, received) = await ConnectAsync(setup);
        using var _ = device;
        await device.PublishStringAsync("tele/rf-tas-console/LWT", "Online", retain: true);

        using var sent = await admin.PostAsJsonAsync("/api/devices/rf-tas-console/commands",
            new { name = "tasmota", payload = new { command = "RfSend", value = """{"Data":"A1B2C3"}""" } });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var cid = (await sent.Content.ReadFromJsonAsync<CommandDto>())!.Cid;
        Assert.True(await SeenAsync(received, "cmnd/rf-tas-console/RfSend", """{"Data":"A1B2C3"}"""), "the command reached the device as typed");

        await device.PublishStringAsync("stat/rf-tas-console/RESULT", """{"RfSend":"Done"}""");
        await hive.WaitForAsync(async db =>
            (await db.Commands.SingleAsync(x => x.Cid == cid)).Status == CommandStatus.Ok
            && await db.DeviceLogs.AnyAsync(l => l.DeviceId == "rf-tas-console" && l.Msg.Contains("RfSend")),
            "the answer closed the command and is in the device log");

        // a secret is sent but not kept in the journal
        using var secret = await admin.PostAsJsonAsync("/api/devices/rf-tas-console/commands",
            new { name = "tasmota", payload = new { command = "WebPassword", value = "hunter2-secret" } });
        Assert.Equal(HttpStatusCode.Accepted, secret.StatusCode);
        Assert.True(await SeenAsync(received, "cmnd/rf-tas-console/WebPassword", "hunter2-secret"));
        var commands = await hive.QueryAsync(db => db.Commands.Where(c => c.DeviceId == "rf-tas-console" && c.Name == "tasmota").ToListAsync());
        var journal = commands.Select(c => c.Payload.RootElement.GetRawText()).ToList();
        Assert.DoesNotContain(journal, j => j.Contains("hunter2-secret"));

        // a command that is not a name is refused
        using var bad = await admin.PostAsJsonAsync("/api/devices/rf-tas-console/commands", new { name = "tasmota", payload = new { command = "../x" } });
        Assert.NotEqual(HttpStatusCode.Accepted, bad.StatusCode);
    }
}
