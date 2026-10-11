using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Modules.Devices;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;

namespace Hive.Tests.Integration;

/// <summary>Battery hornets: the sleep interval is set from the UI and the hive notices missed wake-ups.</summary>
[Collection(HiveCollection.Name)]
public sealed class DeviceConfigTests(HiveFixture hive)
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
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.MqttHost, hive.MqttPort).WithClientId($"cfg-{id}").Build());
        await client.SubscribeAsync($"vs/v1/dev/{id}/config");
        for (var i = 0; i < 50 && payload is null; i++)
            await Task.Delay(100);
        return payload ?? throw new TimeoutException($"retained config rev {rev} for {id}");
    }

    [Fact]
    public async Task Sleep_interval_goes_into_the_retained_config()
    {
        await RegisterAsync("meteo-sleep1");
        var admin = await hive.LoginAsync();

        using var response = await admin.PatchAsJsonAsync("/api/devices/meteo-sleep1/config", new { sleepS = 600 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(600, dto.GetProperty("sleepS").GetInt32());

        using var config = JsonDocument.Parse(await RetainedConfigAsync("meteo-sleep1", dto.GetProperty("configRev").GetInt32()));
        Assert.Equal(600, config.RootElement.GetProperty("sleep_s").GetInt32());
        Assert.Equal(60, config.RootElement.GetProperty("health_interval_s").GetInt32());

        // 0 switches battery mode off: the key disappears from the config.
        using var off = await admin.PatchAsJsonAsync("/api/devices/meteo-sleep1/config", new { sleepS = 0 });
        var offDto = await off.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, offDto.GetProperty("sleepS").ValueKind);
        using var offConfig = JsonDocument.Parse(await RetainedConfigAsync("meteo-sleep1", offDto.GetProperty("configRev").GetInt32()));
        Assert.False(offConfig.RootElement.TryGetProperty("sleep_s", out _));
    }

    [Fact]
    public async Task Too_short_sleep_and_viewers_are_refused()
    {
        await RegisterAsync("meteo-sleep2");
        var admin = await hive.LoginAsync();
        using var tooShort = await admin.PatchAsJsonAsync("/api/devices/meteo-sleep2/config", new { sleepS = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        using var unknown = await admin.PatchAsJsonAsync("/api/devices/nobody-here/config", new { sleepS = 600 });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        await hive.CreateUserAsync("viewer-cfg", "viewer-cfg-password", "viewer");
        var viewer = await hive.LoginAsync("viewer-cfg", "viewer-cfg-password");
        using var forbidden = await viewer.PatchAsJsonAsync("/api/devices/meteo-sleep2/config", new { sleepS = 600 });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Sleeping_hornet_that_misses_its_wake_ups_goes_offline()
    {
        await RegisterAsync("meteo-sleep3");
        var admin = await hive.LoginAsync();
        (await admin.PatchAsJsonAsync("/api/devices/meteo-sleep3/config", new { sleepS = 60 })).EnsureSuccessStatusCode();
        var watch = hive.App.Services.GetRequiredService<SleepingDeviceWatch>();

        // Seen recently: still fine while asleep.
        await watch.CheckAsync(CancellationToken.None);
        Assert.Equal(DeviceStatus.Online, await hive.QueryAsync(db => db.Devices.Where(d => d.DeviceId == "meteo-sleep3").Select(d => d.Status).SingleAsync()));

        // Last heard 5 minutes ago with a 60 s interval: three wake-ups missed.
        await hive.QueryAsync(db => db.Devices.Where(d => d.DeviceId == "meteo-sleep3")
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.LastSeenAt, DateTimeOffset.UtcNow.AddMinutes(-5))));
        await watch.CheckAsync(CancellationToken.None);
        Assert.Equal(DeviceStatus.Offline, await hive.QueryAsync(db => db.Devices.Where(d => d.DeviceId == "meteo-sleep3").Select(d => d.Status).SingleAsync()));
    }

    [Theory]
    [InlineData(600, 30, false)]
    [InlineData(600, 1859, false)]
    [InlineData(600, 1861, true)]
    public void Overdue_after_three_missed_wake_ups(int sleepS, int secondsSinceSeen, bool overdue)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(overdue, SleepingDeviceWatch.IsOverdue(sleepS, now.AddSeconds(-secondsSinceSeen), now));
    }
}
