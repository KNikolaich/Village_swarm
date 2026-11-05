using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Contracts;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Modules.Cluster;
using Hive.Modules.Telemetry;
using Hive.Modules.Telemetry.Ingest;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace Hive.Tests.Integration;

/// <summary>The city watches home and keeps its own hornets: they are added, changed and ruled here, the copies of home's stay read-only.</summary>
public sealed class CityOwnHornetsTests(CityFixture city) : IClassFixture<CityFixture>
{
    /// <summary>A hornet that said hello on the city broker without being enrolled here: what a copy of home's looks like.</summary>
    private async Task RegisterCopyAsync(string id)
    {
        var spec = new HornetSpec { Id = id, Type = "meteo" };
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"));
        hornet.Start(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var link = new MqttHornetLink(new BrokerOptions { Host = city.MqttHost, Port = city.MqttPort });
        await link.ConnectAsync(id, CancellationToken.None);
        foreach (var m in hornet.OnConnected(DateTimeOffset.UtcNow))
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
        await city.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == id), "copy of the hornet");
    }

    /// <summary>Home's heartbeat, so that the city stays an observer (without it, hornets that speak to the city broker make it take over the alarms).</summary>
    private async Task BeatUntilAsync(CancellationToken stop)
    {
        using var home = new MqttClientFactory().CreateMqttClient();
        await home.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(city.MqttHost, city.MqttPort).WithClientId($"own-home-{Guid.NewGuid():N}").Build());
        while (!stop.IsCancellationRequested)
        {
            var beat = new Hive.Contracts.Messages.HeartbeatMessage { Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Node = Hive.Contracts.Messages.BrokerNode.Home, Role = Hive.Contracts.Messages.HiveRole.Active, Version = "test" };
            await home.PublishStringAsync("vs/v1/hive/home/heartbeat", JsonSerializer.Serialize(beat, Hive.Contracts.Messages.ContractJson.Options));
            try
            {
                await Task.Delay(500, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SendTemperatureAsync(string id, double value)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(city.MqttHost, city.MqttPort).WithClientId($"{id}-tele-{Guid.NewGuid():N}").Build());
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new { v = 1, id = Ulid.New(now), ts = now.ToUnixTimeMilliseconds(), seq = Random.Shared.Next(1, 1_000_000), boot = 1, value, unit = "C" });
        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic($"vs/v1/dev/{id}/tele/temperature").WithPayload(payload).Build());
        await client.DisconnectAsync();
    }

    private static object Above20(string sensor, string text) => new
    {
        name = $"Above 20 {sensor}",
        enabled = true,
        definition = new
        {
            trigger = new { kind = "telemetry", device = sensor, metric = "temperature" },
            conditions = new object[] { new { kind = "value", device = sensor, metric = "temperature", op = ">", value = 20 } },
            actions = new object[] { new { kind = "notify", text, level = "info" } },
            cooldownS = 1,
        },
    };

    private async Task<bool> RuleEventAsync(string text) => await city.QueryAsync(db =>
        db.Events.AnyAsync(e => e.Type == "rule" && e.Payload.RootElement.GetProperty("text").GetString()!.Contains(text)));

    [Fact]
    public async Task An_observer_city_adds_changes_and_rules_its_own_hornets_and_leaves_the_copies_of_homes_alone()
    {
        using var beating = new CancellationTokenSource();
        var beats = Task.Run(() => BeatUntilAsync(beating.Token));
        var admin = await city.LoginAsync();
        await RegisterCopyAsync("meteo-copy1");

        // its own hornet: the wizard's code, the board trading it for a login (both refused on a plain observer before)
        var own = await new SimProvisioner(admin).EnrollAsync(new HornetSpec { Id = "meteo-own1", Type = "meteo" }, CancellationToken.None);
        Assert.NotNull(own.UploadToken);
        Assert.True((await city.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == "meteo-own1"))).Local);
        Assert.False((await city.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == "meteo-copy1"))).Local);

        // it is changed here; a copy of home's is not
        using var changed = await admin.PatchAsJsonAsync("/api/devices/meteo-own1/config", new { sleepS = 600 });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var refused = await admin.PatchAsJsonAsync("/api/devices/meteo-copy1/config", new { sleepS = 600 });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var renamed = await admin.PutAsJsonAsync("/api/devices/meteo-copy1/name", new { name = "Не трогать" });
        Assert.Equal(HttpStatusCode.Forbidden, renamed.StatusCode);

        // its rules run, the rules about a copy do not
        using var ownRule = await admin.PostAsJsonAsync("/api/rules", Above20("meteo-own1", "Свой шершень города: {value}"));
        Assert.Equal(HttpStatusCode.Created, ownRule.StatusCode);
        using var copyRule = await admin.PostAsJsonAsync("/api/rules", Above20("meteo-copy1", "Копия из дома: {value}"));
        Assert.Equal(HttpStatusCode.Created, copyRule.StatusCode);
        await SendTemperatureAsync("meteo-copy1", 25);
        await SendTemperatureAsync("meteo-own1", 25);
        await city.WaitForAsync(async _ => await RuleEventAsync("Свой шершень города"), "the rule of the city's own hornet ran");
        await Task.Delay(1500);
        Assert.False(await RuleEventAsync("Копия из дома"), "a rule about a copy of home's hornet must not run on the observer");
        await beating.CancelAsync();
        await beats;
    }
    /// <summary>The other hive's broker goes into the configs of the city's own hornets only, with short times so that a hornet carried between the hives settles at once.</summary>
    [Fact]
    public async Task The_dachas_broker_goes_into_the_configs_of_the_citys_own_hornets_with_short_times()
    {
        var admin = await city.LoginAsync();
        await RegisterCopyAsync("meteo-copy2");
        await new SimProvisioner(admin).EnrollAsync(new HornetSpec { Id = "meteo-own2", Type = "meteo" }, CancellationToken.None);

        using var set = await admin.PutAsJsonAsync("/api/failover", new { host = "192.168.10.4", port = 1883 });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);   // allowed on the observer: it names the dacha for the city's own hornets

        var own = (await city.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == "meteo-own2"))).Config!.RootElement.GetProperty("mqtt_city");
        Assert.Equal(("192.168.10.4", 20, 900), (own.GetProperty("host").GetString(), own.GetProperty("after_s").GetInt32(), own.GetProperty("probe_s").GetInt32()));
        Assert.Equal(Hive.Infrastructure.Mqtt.CityBrokerLogin.Password(CityFixture.CityBridgeSecret, "meteo-own2"), own.GetProperty("pass").GetString());

        // a copy of home's hornet is none of the city's business
        var copy = await city.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == "meteo-copy2"));
        Assert.True(copy.Config is null || !copy.Config.RootElement.TryGetProperty("mqtt_city", out _));
    }

    /// <summary>The city names its own hornets; home lets them in and knows them as guests, who are not reported as missing while they are away.</summary>
    [Fact]
    public async Task The_citys_own_hornets_are_named_to_home_which_knows_them_as_guests_who_are_not_missed()
    {
        var admin = await city.LoginAsync();
        await RegisterCopyAsync("meteo-copy3");
        await new SimProvisioner(admin).EnrollAsync(new HornetSpec { Id = "meteo-own3", Type = "meteo" }, CancellationToken.None);
        var own = await city.QueryAsync(db => db.Devices.AsNoTracking().Where(d => d.Local).Select(d => d.DeviceId).ToListAsync());
        Assert.Contains("meteo-own3", own);
        Assert.DoesNotContain("meteo-copy3", own);   // only what the city made itself is named

        var snapshot = new LocalsSnapshot(1, 1, "city", [new LocalHornet("meteo-own3", "meteo", "Тестовый шершень", "esp32c3-mini"), new LocalHornet("meteo-guest9", "meteo", "Гость города", null)]);
        var back = LocalsCodec.Unpack(LocalsCodec.Pack(snapshot))!;
        Assert.Equal(("meteo-guest9", "Гость города"), (back.Devices[1].DeviceId, back.Devices[1].Name));
        Assert.Equal(LocalsCodec.Fingerprint(snapshot), LocalsCodec.Fingerprint(snapshot with { Ts = 99 }));   // a new time is not a change

        // the home side: this test app plays home, with the shared secret
        var hiveOptions = Options.Create(new HiveOptions { Node = "home", Role = "active" });
        var receiver = new LocalsReceiver(Options.Create(new ClusterOptions { Enabled = true, BridgePassword = CityFixture.CityBridgeSecret }), hiveOptions,
            city.App.Services.GetRequiredService<IServiceScopeFactory>(), city.App.Services, TimeProvider.System, NullLogger<LocalsReceiver>.Instance);
        Assert.Equal(1, await receiver.ApplyAsync(snapshot, CancellationToken.None));   // meteo-own3 is known already; the guest is new
        var guest = await city.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == "meteo-guest9"));
        Assert.True(guest.Visitor);
        Assert.Equal(DeviceStatus.Offline, guest.Status);
        Assert.Equal(0, await receiver.ApplyAsync(snapshot, CancellationToken.None));   // and not again

        // an hour of silence from a guest is no news; the same from a hornet of the house is
        await city.QueryAsync(async db =>
        {
            (await db.Devices.SingleAsync(d => d.DeviceId == "meteo-guest9")).LastSeenAt = DateTimeOffset.UtcNow.AddHours(-1);
            (await db.Devices.SingleAsync(d => d.DeviceId == "meteo-copy3")).LastSeenAt = DateTimeOffset.UtcNow.AddHours(-1);
            (await db.Devices.SingleAsync(d => d.DeviceId == "meteo-copy3")).Status = DeviceStatus.Offline;
            await db.SaveChangesAsync();
            return 0;
        });
        var watch = new OfflineWatch(city.App.Services.GetRequiredService<IServiceScopeFactory>(), city.App.Services.GetRequiredService<IngestQueue>(),
            Options.Create(new OfflineWatchOptions()), TimeProvider.System, NullLogger<OfflineWatch>.Instance);
        await watch.CheckAsync(CancellationToken.None);
        Assert.Null((await city.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == "meteo-guest9"))).OfflineReportedAt);
        Assert.NotNull((await city.QueryAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == "meteo-copy3"))).OfflineReportedAt);
    }
}
