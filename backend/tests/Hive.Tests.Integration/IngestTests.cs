using System.Net;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Simulator;
using Hive.Simulator.Roles;
using Microsoft.EntityFrameworkCore;

namespace Hive.Tests.Integration;

/// <summary>Simulator hornets -> Mosquitto -> api ingest -> PostgreSQL (build step 4 acceptance).</summary>
[Collection(HiveCollection.Name)]
public sealed class IngestTests(HiveFixture hive) : IAsyncLifetime
{
    private readonly List<IHornetLink> _links = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var link in _links)
            await link.DisposeAsync();
    }

    private async Task<(VirtualHornet Hornet, IHornetLink Link)> ConnectAsync(HornetSpec spec)
    {
        var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"));
        var now = DateTimeOffset.UtcNow.AddMinutes(-10); // booted earlier, clock synced
        hornet.Start(now);
        var link = new MqttHornetLink(new BrokerOptions { Host = hive.MqttHost, Port = hive.MqttPort });
        _links.Add(link);
        await link.ConnectAsync(spec.Id, CancellationToken.None);
        await PublishAsync(link, hornet.OnConnected(DateTimeOffset.UtcNow));
        return (hornet, link);
    }

    private static async Task PublishAsync(IHornetLink link, IEnumerable<Outgoing> messages)
    {
        foreach (var m in messages)
            await link.PublishAsync(m.Topic, m.Payload, m.Retain, m.Qos1, CancellationToken.None);
    }

    [Fact]
    public async Task Ready_when_database_and_broker_are_up()
    {
        var client = hive.App.CreateClient();
        HttpResponseMessage? response = null;
        for (var i = 0; i < 40 && response?.StatusCode != HttpStatusCode.OK; i++, await Task.Delay(250))
            response = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, response!.StatusCode);
    }

    [Fact]
    public async Task Hornet_announcing_itself_is_registered_online()
    {
        await ConnectAsync(new HornetSpec { Id = "guard-reg1", Type = "guard-cam", Fw = "0.9.0-test" });

        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "guard-reg1" && d.Status == DeviceStatus.Online), "device registered");
        var device = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "guard-reg1"));
        Assert.Equal(("guard-cam", "0.9.0-test", "esp32cam-aithinker"), (device.TypeCode, device.FwVersion, device.Hw));
        Assert.NotNull(device.Mac);
    }

    [Fact]
    public async Task Telemetry_and_health_are_stored()
    {
        var (hornet, link) = await ConnectAsync(new HornetSpec { Id = "meteo-ing1", Type = "meteo", TeleIntervalS = 1 });
        var t = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            hornet.Step(t.AddSeconds(i * 2));
            await PublishAsync(link, hornet.TakePending(connected: true));
        }

        await hive.WaitForAsync(db => db.Telemetry.CountAsync(p => p.DeviceId == "meteo-ing1" && p.Metric == "temperature").ContinueWith(c => c.Result == 3), "3 temperature points");
        await hive.WaitForAsync(db => db.DeviceHealth.AnyAsync(h => h.DeviceId == "meteo-ing1"), "health row");
        var humidity = await hive.QueryAsync(db => db.Telemetry.CountAsync(p => p.DeviceId == "meteo-ing1" && p.Metric == "humidity"));
        Assert.Equal(3, humidity);
        var device = await hive.QueryAsync(db => db.Devices.SingleAsync(d => d.DeviceId == "meteo-ing1"));
        Assert.NotNull(device.Rssi);
    }

    [Fact]
    public async Task Duplicate_events_are_stored_once()
    {
        var (hornet, link) = await ConnectAsync(new HornetSpec { Id = "guard-dup1", Type = "guard-cam", Motion = new MotionSpec { EverySeconds = 0 } });
        ((GuardCamRole)hornet.Role).TriggerMotion(DateTimeOffset.UtcNow);
        var motion = hornet.TakePending(connected: true).Single(m => m.Topic.EndsWith("/event/motion", StringComparison.Ordinal));

        // Same message three times: QoS 1 redelivery, outbox replay, second broker.
        for (var i = 0; i < 3; i++)
            await link.PublishAsync(motion.Topic, motion.Payload, false, true, CancellationToken.None);

        await hive.WaitForAsync(db => db.Events.AnyAsync(e => e.DeviceId == "guard-dup1"), "motion event");
        await Task.Delay(1500); // let any duplicate batches land
        var events = await hive.QueryAsync(db => db.Events.Where(e => e.DeviceId == "guard-dup1").ToListAsync());
        var stored = Assert.Single(events);
        Assert.Equal(("motion", EventSeverity.Alarm), (stored.Type, stored.Severity));
    }

    [Fact]
    public async Task Events_buffered_offline_keep_their_ids_and_device_times()
    {
        var (hornet, link) = await ConnectAsync(new HornetSpec { Id = "guard-buf1", Type = "guard-cam", Motion = new MotionSpec { EverySeconds = 0 } });
        var role = (GuardCamRole)hornet.Role;
        await link.DropAsync(CancellationToken.None);
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "guard-buf1" && d.Status == DeviceStatus.Offline), "LWT marks device offline");

        var outageStart = DateTimeOffset.UtcNow.AddMinutes(-3); // events from 3 minutes ago
        role.TriggerMotion(outageStart);
        role.TriggerMotion(outageStart.AddMinutes(1));
        Assert.Empty(hornet.TakePending(connected: false));

        await link.ConnectAsync("guard-buf1", CancellationToken.None);
        await PublishAsync(link, hornet.OnConnected(DateTimeOffset.UtcNow));

        await hive.WaitForAsync(db => db.Events.CountAsync(e => e.DeviceId == "guard-buf1").ContinueWith(c => c.Result == 2), "2 replayed events");
        var events = await hive.QueryAsync(db => db.Events.Where(e => e.DeviceId == "guard-buf1").OrderBy(e => e.Ts).ToListAsync());
        Assert.True(events[0].Ts < events[0].ReceivedAt.AddMinutes(-2), "replayed event keeps the device time");
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "guard-buf1" && d.Status == DeviceStatus.Online), "back online");
    }

    [Fact]
    public async Task Garbage_does_not_stop_ingest()
    {
        var (hornet, link) = await ConnectAsync(new HornetSpec { Id = "meteo-bad1", Type = "meteo", TeleIntervalS = 1 });
        await link.PublishAsync(Topics.Tele("meteo-bad1", "temperature"), "{not json", false, false, CancellationToken.None);
        await link.PublishAsync("vs/v1/dev/BAD ID/status", "online", false, true, CancellationToken.None);

        hornet.Step(DateTimeOffset.UtcNow);
        await PublishAsync(link, hornet.TakePending(connected: true));

        await hive.WaitForAsync(db => db.Telemetry.AnyAsync(p => p.DeviceId == "meteo-bad1"), "valid telemetry after garbage");
    }

    [Fact]
    public async Task Config_applied_rev_is_recorded()
    {
        var (hornet, link) = await ConnectAsync(new HornetSpec { Id = "heat-cfg1", Type = "heat" });
        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "heat-cfg1"), "device registered");

        hornet.Receive(Topics.Config("heat-cfg1"), """{"v":1,"rev":4,"name":"Обогрев гостиная","health_interval_s":60,"failsafe":{"heater":{"min_c":7,"max_c":24,"max_on_s":7200}}}""");
        hornet.Step(DateTimeOffset.UtcNow);
        await PublishAsync(link, hornet.TakePending(connected: true));

        await hive.WaitForAsync(db => db.Devices.AnyAsync(d => d.DeviceId == "heat-cfg1" && d.ConfigAppliedRev == 4), "config_applied rev 4");
        var state = await hive.QueryAsync(db => db.Devices.Where(d => d.DeviceId == "heat-cfg1").Select(d => d.State).SingleAsync());
        Assert.NotNull(state);
    }
}
