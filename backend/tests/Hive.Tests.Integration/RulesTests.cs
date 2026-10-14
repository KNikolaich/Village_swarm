using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Contracts;
using Hive.Domain;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;
using MQTTnet;

namespace Hive.Tests.Integration;

/// <summary>Rules end to end: stored through the API, set off by real readings, acting on hornets, journalled, replayed.</summary>
[Collection(HiveCollection.Name)]
public sealed class RulesTests(HiveFixture hive)
{
    private async Task RegisterAsync(string id, string type = "meteo")
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

    /// <summary>A temperature reading as the hornet would send it (contracts/schemas/tele.schema.json).</summary>
    private async Task SendTemperatureAsync(string id, double value)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(hive.MqttHost, hive.MqttPort).WithClientId($"{id}-tele-{Guid.NewGuid():N}").Build());
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new { v = 1, id = Ulid.New(now), ts = now.ToUnixTimeMilliseconds(), seq = Random.Shared.Next(1, 1_000_000), boot = 1, value, unit = "C" });
        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic($"vs/v1/dev/{id}/tele/temperature").WithPayload(payload).Build());
        await client.DisconnectAsync();
    }

    private static object HeatBelow(string sensor, string heater, double below) => new
    {
        name = $"Heat {sensor}",
        enabled = true,
        definition = new
        {
            trigger = new { kind = "telemetry", device = sensor, metric = "temperature" },
            conditions = new object[] { new { kind = "value", device = sensor, metric = "temperature", op = "<", value = below } },
            actions = new object[]
            {
                new { kind = "command", device = heater, command = "relay", payload = new { channel = "heater", set = "on", for_s = 3600 } },
                new { kind = "notify", text = "Холодно: {value}°, включил обогрев", level = "info" },
            },
            cooldownS = 3600,
        },
    };

    [Fact]
    public async Task A_cold_reading_switches_the_heater_on_once_and_the_journal_says_so()
    {
        await RegisterAsync("meteo-rule-a");
        await RegisterAsync("heat-rule-a", "heat");
        var admin = await hive.LoginAsync();
        using var created = await admin.PostAsJsonAsync("/api/rules", HeatBelow("meteo-rule-a", "heat-rule-a", 5));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        await SendTemperatureAsync("meteo-rule-a", 12); // warm: nothing happens
        await SendTemperatureAsync("meteo-rule-a", 3);
        await hive.WaitForAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "heat-rule-a" && c.IssuedBy == $"rule:{id}"), "heater command");

        var command = await hive.QueryAsync(db => db.Commands.SingleAsync(c => c.DeviceId == "heat-rule-a" && c.IssuedBy == $"rule:{id}"));
        Assert.Equal("relay", command.Name);
        Assert.Equal("on", command.Payload.RootElement.GetProperty("set").GetString());

        // The cooldown holds a second cold reading back.
        await SendTemperatureAsync("meteo-rule-a", 2);
        await Task.Delay(1500);
        Assert.Equal(1, await hive.QueryAsync(db => db.Commands.CountAsync(c => c.IssuedBy == $"rule:{id}")));

        var runs = await admin.GetFromJsonAsync<JsonElement>($"/api/rules/{id}/runs");
        var fired = runs.EnumerateArray().Single();
        Assert.Equal("fired", fired.GetProperty("outcome").GetString());
        Assert.Equal(3, fired.GetProperty("details").GetProperty("value").GetDouble());

        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.Type == "rule" && e.Payload.RootElement.GetProperty("rule").GetString() == $"Heat meteo-rule-a"), "rule note in the feed");
        var rule = await admin.GetFromJsonAsync<JsonElement>($"/api/rules/{id}");
        Assert.Equal("fired", rule.GetProperty("lastOutcome").GetString());
    }

    [Fact]
    public async Task A_disabled_rule_does_nothing()
    {
        await RegisterAsync("meteo-rule-b");
        await RegisterAsync("heat-rule-b", "heat");
        var admin = await hive.LoginAsync();
        var body = HeatBelow("meteo-rule-b", "heat-rule-b", 5);
        using var created = await admin.PostAsJsonAsync("/api/rules", body);
        var saved = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = saved.GetProperty("id").GetInt32();
        var definition = saved.GetProperty("definition");
        using var disabled = await admin.PutAsJsonAsync($"/api/rules/{id}", new { name = "off", enabled = false, definition });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        await SendTemperatureAsync("meteo-rule-b", 1);
        await Task.Delay(1500);
        Assert.False(await hive.QueryAsync(db => db.Commands.AnyAsync(c => c.IssuedBy == $"rule:{id}")));
    }

    [Fact]
    public async Task The_dry_run_replays_past_readings_without_sending_anything()
    {
        await RegisterAsync("meteo-rule-c");
        var now = DateTimeOffset.UtcNow;
        // Cold in the night (two readings), warm in the day.
        var readings = new[] { (now.AddHours(-10), 2.0), (now.AddHours(-9.9), 1.5), (now.AddHours(-3), 15.0) };
        await hive.QueryAsync(async db =>
        {
            foreach (var (ts, value) in readings)
                await db.Database.ExecuteSqlAsync($"INSERT INTO telemetry (device_id, metric, ts, value, received_at) VALUES ('meteo-rule-c', 'temperature', {ts}, {value}, {ts})");
            return 0;
        });
        var admin = await hive.LoginAsync();
        var definition = new
        {
            trigger = new { kind = "telemetry", device = "meteo-rule-c", metric = "temperature" },
            conditions = new object[] { new { kind = "value", device = "meteo-rule-c", metric = "temperature", op = "<", value = 5 } },
            actions = new object[] { new { kind = "command", device = "heat-nowhere", command = "relay", payload = new { channel = "heater", set = "on" } } },
            cooldownS = 3600,
        };
        using var response = await admin.PostAsJsonAsync("/api/rules/dry-run", new { definition, hours = 24 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(3, result.GetProperty("checked").GetInt32());
        var hit = result.GetProperty("hits").EnumerateArray().Single(); // the second cold reading is within the cooldown
        Assert.Equal("fired", hit.GetProperty("outcome").GetString());
        Assert.Equal(2, hit.GetProperty("value").GetDouble());
        Assert.False(await hive.QueryAsync(db => db.Commands.AnyAsync(c => c.DeviceId == "heat-nowhere")));
    }

    [Fact]
    public async Task Bad_rules_are_refused_and_only_admins_write_them()
    {
        var admin = await hive.LoginAsync();
        using var noActions = await admin.PostAsJsonAsync("/api/rules", new
        {
            name = "empty",
            enabled = true,
            definition = new { trigger = new { kind = "schedule", at = "07:00" }, conditions = Array.Empty<object>(), actions = Array.Empty<object>() },
        });
        Assert.Equal(HttpStatusCode.BadRequest, noActions.StatusCode);

        await hive.CreateUserAsync("viewer-rules", "viewer-rules-password", Hive.Infrastructure.Identity.HiveRoles.Viewer);
        var viewer = await hive.LoginAsync("viewer-rules", "viewer-rules-password");
        using var read = await viewer.GetAsync("/api/rules");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var write = await viewer.PostAsJsonAsync("/api/rules", HeatBelow("x", "y", 5));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
