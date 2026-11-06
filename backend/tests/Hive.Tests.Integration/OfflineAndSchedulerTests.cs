using System.Net.Http.Json;
using System.Text.Json;
using Hive.Domain;
using Hive.Modules.Rules;
using Hive.Modules.Telemetry;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests.Integration;

/// <summary>A hornet that stays away is reported once; time-based rules fire on their own, once per due moment.</summary>
[Collection(HiveCollection.Name)]
public sealed class OfflineAndSchedulerTests(HiveFixture hive)
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

    private Task SetStateAsync(string id, DeviceStatus status, TimeSpan seenAgo) => hive.QueryAsync(async db =>
    {
        var device = await db.Devices.SingleAsync(d => d.DeviceId == id);
        device.Status = status;
        device.LastSeenAt = DateTimeOffset.UtcNow - seenAgo;
        await db.SaveChangesAsync();
        return 0;
    });

    private T Hosted<T>() => hive.App.Services.GetServices<IHostedService>().OfType<T>().Single();

    [Fact]
    public async Task A_hornet_offline_for_a_quarter_of_an_hour_is_reported_once_and_its_return_too()
    {
        await RegisterAsync("meteo-off-a");
        var watch = Hosted<OfflineWatch>();

        await SetStateAsync("meteo-off-a", DeviceStatus.Offline, TimeSpan.FromMinutes(5));
        await watch.CheckAsync(CancellationToken.None);
        Assert.False(await hive.QueryAsync(db => db.Events.AnyAsync(e => e.DeviceId == "meteo-off-a" && e.Type == "device_offline")), "5 minutes is a hiccup, not an outage");

        await SetStateAsync("meteo-off-a", DeviceStatus.Offline, TimeSpan.FromMinutes(20));
        await watch.CheckAsync(CancellationToken.None);
        await watch.CheckAsync(CancellationToken.None);
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == "meteo-off-a" && e.Type == "device_offline"), "offline warning");
        Assert.Equal(1, await hive.QueryAsync(db => db.Events.CountAsync(e => e.DeviceId == "meteo-off-a" && e.Type == "device_offline")));
        var warning = await hive.QueryAsync(db => db.Events.SingleAsync(e => e.DeviceId == "meteo-off-a" && e.Type == "device_offline"));
        Assert.Equal(EventSeverity.Warn, warning.Severity);

        await SetStateAsync("meteo-off-a", DeviceStatus.Online, TimeSpan.Zero);
        await watch.CheckAsync(CancellationToken.None);
        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == "meteo-off-a" && e.Type == "device_online"), "back online");
    }

    private async Task<int> CreateRuleAsync(HttpClient admin, string name, object trigger)
    {
        using var response = await admin.PostAsJsonAsync("/api/rules", new
        {
            name,
            enabled = true,
            definition = new
            {
                trigger,
                conditions = Array.Empty<object>(),
                actions = new object[] { new { kind = "notify", text = "tick", level = "info" } },
                cooldownS = 0,
            },
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private Task<int> RunsAsync(int ruleId) => hive.QueryAsync(db => db.RuleRuns.CountAsync(r => r.RuleId == ruleId && r.Outcome == "fired"));

    [Fact]
    public async Task A_schedule_rule_fires_when_its_time_has_come_and_only_once()
    {
        var admin = await hive.LoginAsync();
        var scheduler = Hosted<RuleScheduler>();
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddMinutes(-2), TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"));
        var id = await CreateRuleAsync(admin, "Every day soon", new { kind = "schedule", at = local.ToString("HH:mm") });

        await scheduler.TickAsync(CancellationToken.None);
        await scheduler.TickAsync(CancellationToken.None);

        Assert.Equal(1, await RunsAsync(id));
    }

    [Fact]
    public async Task A_schedule_in_the_far_future_of_the_day_does_not_fire_yet_and_an_offline_rule_fires_once_per_outage()
    {
        var admin = await hive.LoginAsync();
        var scheduler = Hosted<RuleScheduler>();
        await RegisterAsync("meteo-off-b");

        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddHours(3), TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"));
        var later = await CreateRuleAsync(admin, "Later", new { kind = "schedule", at = local.ToString("HH:mm") });
        var gone = await CreateRuleAsync(admin, "Gone", new { kind = "offline", device = "meteo-off-b", offlineMin = 15 });

        await SetStateAsync("meteo-off-b", DeviceStatus.Offline, TimeSpan.FromMinutes(30));
        await scheduler.TickAsync(CancellationToken.None);
        await scheduler.TickAsync(CancellationToken.None);

        Assert.Equal(0, await RunsAsync(later));
        Assert.Equal(1, await RunsAsync(gone));
    }
}
