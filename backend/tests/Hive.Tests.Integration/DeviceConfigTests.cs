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

    [Fact]
    public async Task Light_sleepers_stay_connected_and_are_not_judged_by_wake_ups()
    {
        await RegisterAsync("meteo-sleep4");
        var admin = await hive.LoginAsync();
        using var response = await admin.PatchAsJsonAsync("/api/devices/meteo-sleep4/config", new { sleepS = 300, sleepMode = "light" });
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("light", dto.GetProperty("sleepMode").GetString());
        using var config = JsonDocument.Parse(await RetainedConfigAsync("meteo-sleep4", dto.GetProperty("configRev").GetInt32()));
        Assert.Equal("light", config.RootElement.GetProperty("sleep_mode").GetString());

        // A light sleeper keeps its LWT, so an old last-seen does not make it offline.
        await hive.QueryAsync(db => db.Devices.Where(d => d.DeviceId == "meteo-sleep4")
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.LastSeenAt, DateTimeOffset.UtcNow.AddHours(-1))));
        await hive.App.Services.GetRequiredService<SleepingDeviceWatch>().CheckAsync(CancellationToken.None);
        Assert.Equal(DeviceStatus.Online, await hive.QueryAsync(db => db.Devices.Where(d => d.DeviceId == "meteo-sleep4").Select(d => d.Status).SingleAsync()));
    }

    [Fact]
    public async Task Rename_changes_the_name_and_the_retained_config()
    {
        await RegisterAsync("meteo-rename1");
        var admin = await hive.LoginAsync();
        // Gives the hornet a retained config, the way an enrolled one has.
        (await admin.PatchAsJsonAsync("/api/devices/meteo-rename1/config", new { sleepS = 600 })).EnsureSuccessStatusCode();

        using var response = await admin.PutAsJsonAsync("/api/devices/meteo-rename1/name", new { name = "  Теплица у бани  " });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Теплица у бани", dto.GetProperty("name").GetString());
        Assert.Equal("Теплица у бани", await hive.QueryAsync(db => db.Devices.Where(d => d.DeviceId == "meteo-rename1").Select(d => d.Name).SingleAsync()));

        // The config carries the name too, with the other settings kept.
        using var config = JsonDocument.Parse(await RetainedConfigAsync("meteo-rename1", dto.GetProperty("configRev").GetInt32()));
        Assert.Equal("Теплица у бани", config.RootElement.GetProperty("name").GetString());
        Assert.Equal(600, config.RootElement.GetProperty("sleep_s").GetInt32());
    }

    [Fact]
    public async Task Empty_names_unknown_hornets_and_members_are_refused_rename()
    {
        await RegisterAsync("meteo-rename2");
        var admin = await hive.LoginAsync();
        using var empty = await admin.PutAsJsonAsync("/api/devices/meteo-rename2/name", new { name = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var tooLong = await admin.PutAsJsonAsync("/api/devices/meteo-rename2/name", new { name = new string('x', DeviceConfigService.MaxNameLength + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        using var unknown = await admin.PutAsJsonAsync("/api/devices/nobody-here/name", new { name = "Кто-то" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        await hive.CreateUserAsync("member-rename", "member-rename-password", "member");
        var member = await hive.LoginAsync("member-rename", "member-rename-password");
        using var forbidden = await member.PutAsJsonAsync("/api/devices/meteo-rename2/name", new { name = "Чужое имя" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
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
    /// <summary>A hornet with a screen may show another device's reading: the hive keeps the choice in the config and feeds the ready text.</summary>
    [Fact]
    public async Task A_screen_shows_readings_of_other_devices_as_ready_texts()
    {
        await RegisterAsync("meteo-disp-a");
        await RegisterAsync("meteo-disp-b");
        await hive.QueryAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO telemetry (device_id, metric, ts, value, received_at)
                VALUES ('meteo-disp-b', 'temperature', now(), -3.24, now())
                """);
            return 0;
        });
        var admin = await hive.LoginAsync();

        using var client = new MqttClientFactory().CreateMqttClient();
        var texts = new List<string>();
        client.ApplicationMessageReceivedAsync += m =>
        {
            lock (texts)
                texts.Add(m.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
        };
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.MqttHost, hive.MqttPort).WithClientId("disp-watch").Build());
        await client.SubscribeAsync("vs/v1/hive/display/meteo-disp-a");

        using var tooMany = await admin.PutAsJsonAsync("/api/devices/meteo-disp-a/display",
            new { items = Enumerable.Range(0, 5).Select(_ => new { metric = "temperature" }).ToArray() });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        using var saved = await admin.PutAsJsonAsync("/api/devices/meteo-disp-a/display",
            new { items = new object[] { new { device = "meteo-disp-b", metric = "temperature", label = "Улица" }, new { metric = "humidity" } } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        JsonElement Last()
        {
            lock (texts)
                return JsonDocument.Parse(texts.Last()).RootElement.Clone();
        }
        for (var i = 0; i < 50 && texts.Count == 0; i++)
            await Task.Delay(100);
        var message = Last();
        var items = message.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(("Улица", "-3.2°"), (items[0].GetProperty("label").GetString(), items[0].GetProperty("text").GetString()));
        Assert.Equal("--", items[1].GetProperty("text").GetString());   // no humidity of its own yet: no number is better than a wrong one
        Assert.False(items[1].TryGetProperty("label", out _));

        var back = await admin.GetFromJsonAsync<JsonElement>("/api/devices/meteo-disp-a/display");
        Assert.Equal(2, back.GetProperty("items").GetArrayLength());
        var config = await hive.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == "meteo-disp-a"));
        Assert.Equal("meteo-disp-b", config.Config!.RootElement.GetProperty("display").GetProperty("items")[0].GetProperty("device").GetString());

        // no choice any more: an empty list goes out, so the hornet shows its own readings again
        var before = texts.Count;
        using var cleared = await admin.PutAsJsonAsync("/api/devices/meteo-disp-a/display", new { items = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        for (var i = 0; i < 50 && texts.Count == before; i++)
            await Task.Delay(100);
        Assert.Equal(0, Last().GetProperty("items").GetArrayLength());
    }
}
