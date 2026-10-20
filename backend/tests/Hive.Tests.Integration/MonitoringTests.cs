using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Contracts;
using Hive.Domain;
using Hive.Modules.Telemetry;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;

namespace Hive.Tests.Integration;

/// <summary>Monitoring mode: frequent readings for a while, alarms on limits and silence, the usual interval back at the end.</summary>
[Collection(HiveCollection.Name)]
public sealed class MonitoringTests(HiveFixture hive)
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

    private async Task SendTemperatureAsync(string id, double value)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.MqttHost, hive.MqttPort).WithClientId($"{id}-tele-{Guid.NewGuid():N}").Build());
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new { v = 1, id = Ulid.New(now), ts = now.ToUnixTimeMilliseconds(), seq = Random.Shared.Next(1, 1_000_000), boot = 1, value, unit = "C" });
        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic($"vs/v1/dev/{id}/tele/temperature").WithPayload(payload).Build());
        await client.DisconnectAsync();
    }

    private Task<int> CountAsync(string id, string type) => hive.QueryAsync(db => db.Events.CountAsync(e => e.DeviceId == id && e.Type == type));

    private Task<int?> TeleIntervalAsync(string id) =>
        hive.QueryAsync(async db => MonitoringService.TeleIntervalS(await db.Devices.SingleAsync(d => d.DeviceId == id)));

    private Task ChangeAsync(string id, Func<MonitorState, MonitorState> change) => hive.QueryAsync(async db =>
    {
        var device = await db.Devices.SingleAsync(d => d.DeviceId == id);
        MonitorState.Write(device, change(MonitorState.Read(device)!));
        return await db.SaveChangesAsync();
    });

    private MonitoringWatch Watch() => hive.App.Services.GetServices<IHostedService>().OfType<MonitoringWatch>().Single();

    [Fact]
    public async Task Limits_raise_an_alarm_once_and_the_all_clear_after_the_hysteresis()
    {
        await RegisterAsync("meteo-mon-a");
        var admin = await hive.LoginAsync();

        using var start = await admin.PutAsJsonAsync("/api/telemetry/monitoring/meteo-mon-a", new { hours = 2, intervalS = 5, below = 110.0, above = 130.0 });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var dto = await start.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, dto.GetProperty("intervalS").GetInt32());
        Assert.Equal("temperature", dto.GetProperty("metric").GetString());
        Assert.InRange(dto.GetProperty("until").GetDateTimeOffset() - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(119), TimeSpan.FromMinutes(121));
        Assert.Equal(5, await TeleIntervalAsync("meteo-mon-a"));
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/telemetry/monitoring");
        Assert.Contains(list.EnumerateArray(), m => m.GetProperty("deviceId").GetString() == "meteo-mon-a");

        await SendTemperatureAsync("meteo-mon-a", 120);
        await SendTemperatureAsync("meteo-mon-a", 104.5);
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == "meteo-mon-a" && e.Type == "monitor_threshold"), "threshold alarm");
        await SendTemperatureAsync("meteo-mon-a", 103);
        await SendTemperatureAsync("meteo-mon-a", 110.2); // back over the line, but not clear of it
        await hive.WaitForAsync(db => db.Telemetry.AnyAsync(t => t.DeviceId == "meteo-mon-a" && t.Value == 110.2), "reading in");
        await Task.Delay(1500);
        Assert.Equal(1, await CountAsync("meteo-mon-a", "monitor_threshold"));
        Assert.Equal(0, await CountAsync("meteo-mon-a", "monitor_normal"));
        var alarm = await hive.QueryAsync(db => db.Events.SingleAsync(e => e.DeviceId == "meteo-mon-a" && e.Type == "monitor_threshold"));
        Assert.Equal(EventSeverity.Alarm, alarm.Severity);
        Assert.Equal(110, alarm.Payload.RootElement.GetProperty("limit").GetDouble());

        await SendTemperatureAsync("meteo-mon-a", 111);
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == "meteo-mon-a" && e.Type == "monitor_normal"), "all clear");

        using var stop = await admin.DeleteAsync("/api/telemetry/monitoring/meteo-mon-a");
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        Assert.Null(await TeleIntervalAsync("meteo-mon-a")); // it had none: back to the firmware default
        Assert.Null(await hive.QueryAsync(async db => (await db.Devices.SingleAsync(d => d.DeviceId == "meteo-mon-a")).Monitor));
        using var again = await admin.DeleteAsync("/api/telemetry/monitoring/meteo-mon-a");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Silence_raises_an_alarm_and_the_end_gives_the_usual_interval_back()
    {
        await RegisterAsync("meteo-mon-b");
        var admin = await hive.LoginAsync();
        using (var tele = await admin.PatchAsJsonAsync("/api/devices/meteo-mon-b/config", new { teleIntervalS = 300 }))
            Assert.Equal(HttpStatusCode.OK, tele.StatusCode);
        using (var start = await admin.PutAsJsonAsync("/api/telemetry/monitoring/meteo-mon-b", new { hours = 0.5, intervalS = 10 }))
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal(10, await TeleIntervalAsync("meteo-mon-b"));

        // Started a minute ago and nothing came since: three intervals of silence.
        await ChangeAsync("meteo-mon-b", s => s with { StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await Watch().CheckAsync(CancellationToken.None);
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == "meteo-mon-b" && e.Type == "monitor_stale"), "silence alarm");

        await SendTemperatureAsync("meteo-mon-b", 21.5);
        await hive.WaitForAsync(db => db.Telemetry.AnyAsync(t => t.DeviceId == "meteo-mon-b" && t.Value == 21.5), "reading in");
        await Watch().CheckAsync(CancellationToken.None);
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == "meteo-mon-b" && e.Type == "monitor_resumed"), "back");

        await ChangeAsync("meteo-mon-b", s => s with { Until = DateTimeOffset.UtcNow.AddSeconds(-1) });
        await Watch().CheckAsync(CancellationToken.None);
        Assert.Equal(300, await TeleIntervalAsync("meteo-mon-b"));
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/telemetry/monitoring");
        Assert.DoesNotContain(list.EnumerateArray(), m => m.GetProperty("deviceId").GetString() == "meteo-mon-b");
    }

    [Fact]
    public async Task Odd_requests_and_sleeping_hornets_are_refused()
    {
        await RegisterAsync("meteo-mon-c");
        var admin = await hive.LoginAsync();
        using (var hours = await admin.PutAsJsonAsync("/api/telemetry/monitoring/meteo-mon-c", new { hours = 5, intervalS = 10 }))
            Assert.Equal(HttpStatusCode.BadRequest, hours.StatusCode);
        using (var interval = await admin.PutAsJsonAsync("/api/telemetry/monitoring/meteo-mon-c", new { hours = 2, intervalS = 7 }))
            Assert.Equal(HttpStatusCode.BadRequest, interval.StatusCode);
        using (var limits = await admin.PutAsJsonAsync("/api/telemetry/monitoring/meteo-mon-c", new { hours = 2, intervalS = 10, below = 50.0, above = 40.0 }))
            Assert.Equal(HttpStatusCode.BadRequest, limits.StatusCode);
        using (var nobody = await admin.PutAsJsonAsync("/api/telemetry/monitoring/nobody-here", new { hours = 2, intervalS = 10 }))
            Assert.Equal(HttpStatusCode.NotFound, nobody.StatusCode);

        using (var sleep = await admin.PatchAsJsonAsync("/api/devices/meteo-mon-c/config", new { sleepS = 600 }))
            Assert.Equal(HttpStatusCode.OK, sleep.StatusCode);
        using var sleeping = await admin.PutAsJsonAsync("/api/telemetry/monitoring/meteo-mon-c", new { hours = 2, intervalS = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, sleeping.StatusCode);
    }
}
