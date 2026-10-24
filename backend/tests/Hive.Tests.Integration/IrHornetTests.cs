using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Contracts;
using Hive.Domain;
using Hive.Modules.Devices;
using Microsoft.EntityFrameworkCore;
using Hive.Simulator;
using Hive.Simulator.Roles;
using MQTTnet;

namespace Hive.Tests.Integration;

/// <summary>Our own infrared hornets: a receiver reports the code of a remote's button, a transmitter sends it back by the name the family gave it.</summary>
[Collection(HiveCollection.Name)]
public sealed class IrHornetTests(HiveFixture hive)
{
    /// <summary>A hornet that said hello (as any does), made the type of this role: the firmware is not here to say it.</summary>
    private async Task AddAsync(string id, string type)
    {
        var spec = new HornetSpec { Id = id, Type = "meteo" };
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"));
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        await link.ConnectAsync(id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == id && d.Status == DeviceStatus.Online), "device online");
        await hive.QueryAsync(async db =>
        {
            (await db.Devices.SingleAsync(d => d.DeviceId == id)).TypeCode = type;
            await db.SaveChangesAsync();
            return 0;
        });
    }

    [Fact]
    public async Task A_code_the_receiver_saw_becomes_a_button_of_the_transmitter_and_goes_out_as_its_code()
    {
        await AddAsync("irrx-hall", "irrx");
        await AddAsync("irtx-hall", "irtx");
        var admin = await hive.LoginAsync();

        using var radio = new MqttClientFactory().CreateMqttClient();
        string? sent = null;
        radio.ApplicationMessageReceivedAsync += m =>
        {
            sent = m.ApplicationMessage.ConvertPayloadToString();
            return Task.CompletedTask;
        };
        await radio.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.MqttHost, hive.MqttPort).WithClientId("ir-hall-radio").Build());
        await radio.SubscribeAsync("vs/v1/dev/irtx-hall/cmd/ir");

        // the receiver keeps the last code in its retained state: that is what the screen offers to learn from
        var state = new Hive.Contracts.Messages.StateMessage
        {
            Id = Ulid.New(), Seq = 1, Boot = 1, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            State = new Dictionary<string, JsonElement> { ["ir_last"] = JsonSerializer.SerializeToElement("NEC 32 0x02FD807F") },
        };
        await radio.PublishStringAsync("vs/v1/dev/irrx-hall/state", JsonSerializer.Serialize(state, Hive.Contracts.Messages.ContractJson.Options), retain: true);
        await hive.WaitForAsync(async db =>
            (await db.Devices.SingleAsync(d => d.DeviceId == "irrx-hall")).State?.RootElement.GetProperty("ir_last").GetString() == "NEC 32 0x02FD807F",
            "last infrared code kept");

        // the buttons belong to the transmitter
        using var saved = await admin.PutAsJsonAsync("/api/devices/irtx-hall/ir-buttons",
            new { buttons = new Dictionary<string, object> { ["Убираться"] = new { protocol = "nec", bits = 32, data = "0x02FD807F" } } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var read = await admin.GetFromJsonAsync<IrButtonsDto>("/api/devices/irtx-hall/ir-buttons");
        Assert.Equal(("NEC", 32, "0x02FD807F"), (read!.Buttons["Убираться"].Protocol, read.Buttons["Убираться"].Bits, read.Buttons["Убираться"].Data));

        using var press = await admin.PostAsJsonAsync("/api/devices/irtx-hall/commands", new { name = "ir", payload = new { button = "Убираться" } });
        Assert.Equal(HttpStatusCode.Accepted, press.StatusCode);
        for (var i = 0; i < 50 && sent is null; i++)
            await Task.Delay(100);
        using var cmd = JsonDocument.Parse(sent ?? throw new TimeoutException("the transmitter never got cmd/ir"));
        Assert.Equal(("NEC", 32, "0x02FD807F"), (cmd.RootElement.GetProperty("protocol").GetString(), cmd.RootElement.GetProperty("bits").GetInt32(), cmd.RootElement.GetProperty("data").GetString()));
        Assert.False(cmd.RootElement.TryGetProperty("button", out _));   // the hornet gets the code, not the name

        // a button nobody taught is refused; so is a code that cannot be sent; and a receiver has no remote to keep
        using var unknown = await admin.PostAsJsonAsync("/api/devices/irtx-hall/commands", new { name = "ir", payload = new { button = "Нет такой" } });
        Assert.NotEqual(HttpStatusCode.Accepted, unknown.StatusCode);
        using var bad = await admin.PutAsJsonAsync("/api/devices/irtx-hall/ir-buttons",
            new { buttons = new Dictionary<string, object> { ["X"] = new { protocol = "UNKNOWN", bits = 32, data = "0x1" } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/devices/irrx-hall/ir-buttons")).StatusCode);
    }
}
