using System.Net;
using System.Net.Http.Json;
using Hive.Domain;
using Hive.Modules.Devices;
using Hive.Simulator;
using Microsoft.EntityFrameworkCore;
using MQTTnet;

namespace Hive.Tests.Integration;

/// <summary>An infrared remote on a Tasmota device: learn a code by pressing the remote, name it, send it back.</summary>
public sealed class TasmotaIrTests(DynSecFixture hive) : IClassFixture<DynSecFixture>
{
    [Fact]
    public async Task A_learned_button_is_sent_as_its_code_and_the_remote_can_be_learned_from_the_device()
    {
        var admin = hive.App.CreateClient();
        await new SimProvisioner(admin).LoginAsync("admin", HiveFixture.AdminPassword, CancellationToken.None);
        using var created = await admin.PostAsJsonAsync("/api/provision/tasmota", new { deviceId = "ir-tas-hall", type = "relay", name = "ИК у обогревателя", ir = true });
        var setup = (await created.Content.ReadFromJsonAsync<TasmotaSetup>())!;

        var received = new List<(string Topic, string Payload)>();
        using var device = new MqttClientFactory().CreateMqttClient();
        device.ApplicationMessageReceivedAsync += m =>
        {
            lock (received) received.Add((m.ApplicationMessage.Topic, m.ApplicationMessage.ConvertPayloadToString()));
            return Task.CompletedTask;
        };
        await device.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.Broker.Host, hive.Broker.Port)
            .WithCredentials(setup.Mqtt.User, setup.Mqtt.Pass).WithClientId(setup.DeviceId).Build());
        await device.SubscribeAsync("cmnd/ir-tas-hall/#");
        await device.PublishStringAsync("tele/ir-tas-hall/LWT", "Online", retain: true);

        // press the remote's button: the device reports the code, the hive keeps the last one
        await device.PublishStringAsync("tele/ir-tas-hall/RESULT", """{"IrReceived":{"Protocol":"NEC","Bits":32,"Data":"0x02FD807F"}}""");
        await hive.WaitForAsync(async db =>
            (await db.Devices.SingleAsync(x => x.DeviceId == "ir-tas-hall")).State?.RootElement.GetProperty("ir_last").GetString() == "NEC 32 0x02FD807F",
            "last infrared code kept");

        using var saved = await admin.PutAsJsonAsync("/api/devices/ir-tas-hall/ir-buttons",
            new { buttons = new Dictionary<string, object> { ["Питание"] = new { protocol = "nec", bits = 32, data = "0x02FD807F" } } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var read = await admin.GetFromJsonAsync<IrButtonsDto>("/api/devices/ir-tas-hall/ir-buttons");
        Assert.Equal("NEC", read!.Buttons["Питание"].Protocol);

        using var sent = await admin.PostAsJsonAsync("/api/devices/ir-tas-hall/commands", new { name = "ir", payload = new { button = "Питание" } });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var cid = (await sent.Content.ReadFromJsonAsync<CommandDto>())!.Cid;
        await WaitForAsync(received, "cmnd/ir-tas-hall/IRSend", """{"Protocol":"NEC","Bits":32,"Data":"0x02FD807F","Repeat":0}""");
        await device.PublishStringAsync("stat/ir-tas-hall/RESULT", """{"IRSend":"Done"}""");
        await hive.WaitForAsync(async db => (await db.Commands.SingleAsync(x => x.Cid == cid)).Status == CommandStatus.Ok, "send acknowledged");

        // a button that was never learned is refused, and so is a code that cannot be sent
        using var unknown = await admin.PostAsJsonAsync("/api/devices/ir-tas-hall/commands", new { name = "ir", payload = new { button = "Нет такой" } });
        Assert.NotEqual(HttpStatusCode.Accepted, unknown.StatusCode);
        using var bad = await admin.PutAsJsonAsync("/api/devices/ir-tas-hall/ir-buttons",
            new { buttons = new Dictionary<string, object> { ["X"] = new { protocol = "UNKNOWN", bits = 32, data = "0x1" } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        // buttons exist for a Tasmota device only
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/devices/no-such-device/ir-buttons")).StatusCode);
    }

    private static async Task WaitForAsync(List<(string Topic, string Payload)> received, string topic, string payload)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (received)
                if (received.Any(r => r.Topic == topic && r.Payload == payload))
                    return;
            await Task.Delay(100);
        }
        Assert.Fail($"The device never received {topic} {payload}");
    }
}
