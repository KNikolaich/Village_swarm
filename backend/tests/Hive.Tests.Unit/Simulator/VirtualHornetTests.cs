using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Contracts;
using Hive.Contracts.Mqtt;
using Hive.Simulator;
using Hive.Simulator.Roles;

namespace Hive.Tests.Unit.Simulator;

public class VirtualHornetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static (VirtualHornet Hornet, GuardCamRole Role) GuardCam(string uploadUrl = "")
    {
        var spec = new HornetSpec { Id = "guard-gate1", Type = "guard-cam", Motion = new MotionSpec { EverySeconds = 0, UploadUrl = uploadUrl } };
        var role = new GuardCamRole(spec.Motion, new PhotoLibrary([[0xFF, 0xD8, 0xFF, 0xD9]]));
        var hornet = new VirtualHornet(spec, role, random: new Random(1));
        hornet.Start(T0);
        return (hornet, role);
    }

    private static VirtualHornet Heat(double startC = 15)
    {
        var spec = new HornetSpec { Id = "heat-living1", Type = "heat", Heater = new HeaterSpec { StartC = startC } };
        var hornet = new VirtualHornet(spec, new HeatRole(spec.Heater, 60), random: new Random(1));
        hornet.Start(T0);
        return hornet;
    }

    private static string Cmd(DateTimeOffset ts, int ttlS = 30, string? cid = null, string extra = "") =>
        $$"""{"v":1,"cid":"{{cid ?? Ulid.New(ts)}}","ts":{{ts.ToUnixTimeMilliseconds()}},"ttl_s":{{ttlS}},"by":"user:test"{{extra}}}""";

    private static JsonElement Ack(IEnumerable<Outgoing> messages, string name) =>
        JsonDocument.Parse(messages.Single(m => m.Topic.EndsWith($"/cmd/{name}/ack", StringComparison.Ordinal)).Payload).RootElement;

    private static void AssertMatchesContract(IEnumerable<Outgoing> messages)
    {
        foreach (var m in messages)
        {
            Assert.NotNull(Topics.TryGetDeviceId(m.Topic));
            var schema = ContractSchemas.ForTopic(m.Topic);
            if (schema is null)
                continue;
            var result = ContractSchemas.Evaluate(schema, JsonNode.Parse(m.Payload));
            Assert.True(result.IsValid, $"{m.Topic} vs {schema}: {ContractSchemas.Describe(result)}\n{m.Payload}");
        }
    }

    [Fact]
    public void Connect_publishes_retained_status_and_info()
    {
        var (hornet, _) = GuardCam();
        var messages = hornet.OnConnected(T0).ToList();

        Assert.Equal(("vs/v1/dev/guard-gate1/status", "online", true), (messages[0].Topic, messages[0].Payload, messages[0].Retain));
        Assert.Equal("vs/v1/dev/guard-gate1/info", messages[1].Topic);
        Assert.True(messages[1].Retain);
        AssertMatchesContract(messages);
    }

    [Fact]
    public void Clock_is_unsynced_right_after_boot()
    {
        var (hornet, _) = GuardCam();
        Assert.Equal(0, hornet.NextEnvelope(T0.AddSeconds(1)).Ts);
        Assert.Equal(T0.AddSeconds(10).ToUnixTimeMilliseconds(), hornet.NextEnvelope(T0.AddSeconds(10)).Ts);
    }

    [Fact]
    public void Motion_when_armed_emits_alarm_with_photo_ids_and_queues_uploads()
    {
        var (hornet, role) = GuardCam("http://hive.test");
        role.TriggerMotion(T0.AddSeconds(10));
        var messages = hornet.TakePending(connected: true);

        var motion = JsonDocument.Parse(messages.Single(m => m.Topic.EndsWith("/event/motion", StringComparison.Ordinal)).Payload).RootElement;
        var eventId = motion.GetProperty("id").GetString();
        Assert.Equal([$"{eventId}-0", $"{eventId}-1", $"{eventId}-2"], motion.GetProperty("photos").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("uploading", motion.GetProperty("photo_status").GetString());
        Assert.Equal(3, role.TakeUploads().Count);
        AssertMatchesContract(messages);
    }

    [Fact]
    public void Motion_respects_cooldown()
    {
        var (hornet, role) = GuardCam();
        role.TriggerMotion(T0.AddSeconds(10));
        role.TriggerMotion(T0.AddSeconds(15));
        Assert.Single(hornet.TakePending(connected: true), m => m.Topic.EndsWith("/event/motion", StringComparison.Ordinal));
    }

    [Fact]
    public void Expired_command_is_rejected_with_expired()
    {
        var (hornet, role) = GuardCam();
        hornet.Receive(Topics.Cmd("guard-gate1", "arm"), Cmd(T0.AddSeconds(-60), ttlS: 30, extra: ""","armed":false"""));
        hornet.Step(T0);
        var messages = hornet.TakePending(connected: true);

        Assert.Equal("expired", Ack(messages, "arm").GetProperty("status").GetString());
        Assert.True(role.Armed);
        AssertMatchesContract(messages);
    }

    [Fact]
    public void Repeated_cid_is_not_executed_twice_and_gets_the_same_ack()
    {
        var hornet = Heat();
        var cid = Ulid.New(T0);
        var on = Cmd(T0, cid: cid, extra: ""","channel":"heater","set":"on","for_s":600""");

        hornet.Receive(Topics.Cmd("heat-living1", "relay"), on);
        hornet.Step(T0.AddSeconds(1));
        var first = hornet.TakePending(connected: true);
        hornet.Receive(Topics.Cmd("heat-living1", "relay"), on);
        hornet.Step(T0.AddSeconds(2));
        var second = hornet.TakePending(connected: true);

        Assert.Equal(Ack(first, "relay").GetRawText(), Ack(second, "relay").GetRawText());
        Assert.DoesNotContain(second, m => m.Topic.EndsWith("/state", StringComparison.Ordinal));
        AssertMatchesContract(first);
    }

    [Fact]
    public void Relay_on_above_max_c_is_rejected_by_failsafe()
    {
        var hornet = Heat(startC: 26);
        hornet.Receive(Topics.Cmd("heat-living1", "relay"), Cmd(T0, extra: ""","channel":"heater","set":"on" """));
        hornet.Step(T0.AddSeconds(1));
        var messages = hornet.TakePending(connected: true);

        var ack = Ack(messages, "relay");
        Assert.Equal("rejected", ack.GetProperty("status").GetString());
        Assert.StartsWith("failsafe", ack.GetProperty("msg").GetString());
        AssertMatchesContract(messages);
    }

    [Fact]
    public void Unknown_command_is_unsupported()
    {
        var (hornet, _) = GuardCam();
        hornet.Receive(Topics.Cmd("guard-gate1", "dance"), Cmd(T0));
        hornet.Step(T0.AddSeconds(1));
        Assert.Equal("unsupported", Ack(hornet.TakePending(connected: true), "dance").GetProperty("status").GetString());
    }

    [Fact]
    public void Config_is_applied_once_per_rev_and_confirmed()
    {
        var (hornet, role) = GuardCam();
        const string config = """{"v":1,"rev":7,"name":"Камера у ворот","health_interval_s":30,"guard":{"armed":false,"cooldown_s":5}}""";
        hornet.Receive(Topics.Config("guard-gate1"), config);
        hornet.Receive(Topics.Config("guard-gate1"), config);
        hornet.Step(T0.AddSeconds(10));
        var messages = hornet.TakePending(connected: true);

        var applied = Assert.Single(messages, m => m.Topic.EndsWith("/event/config_applied", StringComparison.Ordinal));
        Assert.Equal(7, JsonDocument.Parse(applied.Payload).RootElement.GetProperty("rev").GetInt32());
        Assert.False(role.Armed);
        Assert.Equal(30, hornet.HealthIntervalS);
        AssertMatchesContract(messages);
    }

    [Fact]
    public void Offline_events_are_buffered_and_replayed_with_original_ids_after_reconnect()
    {
        var (hornet, role) = GuardCam();
        hornet.Step(T0.AddSeconds(10)); // health, dropped while offline
        role.TriggerMotion(T0.AddSeconds(10));
        role.TriggerMotion(T0.AddSeconds(40));
        var whileOffline = hornet.TakePending(connected: false);
        Assert.Empty(whileOffline);
        Assert.Equal(2, hornet.OutboxCount);

        var replay = hornet.OnConnected(T0.AddSeconds(60)).ToList();
        var motions = replay.Where(m => m.Topic.EndsWith("/event/motion", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, motions.Count);
        Assert.All(motions, m => Assert.NotEqual(0, JsonDocument.Parse(m.Payload).RootElement.GetProperty("ts").GetInt64()));
        Assert.DoesNotContain(replay, m => m.Topic.EndsWith("/health", StringComparison.Ordinal));
        Assert.Equal(0, hornet.OutboxCount);
        AssertMatchesContract(replay);
    }

    [Fact]
    public void Reboot_increments_boot_and_restarts_seq()
    {
        var (hornet, _) = GuardCam();
        hornet.NextEnvelope(T0);
        hornet.NextEnvelope(T0);
        hornet.Reboot(T0.AddSeconds(30), "panic");
        var e = hornet.NextEnvelope(T0.AddSeconds(31));
        Assert.Equal((2, 1L), (e.Boot, e.Seq));
    }

    [Fact]
    public void Reboot_command_acks_then_requests_reboot()
    {
        var (hornet, _) = GuardCam();
        hornet.Receive(Topics.Cmd("guard-gate1", "reboot"), Cmd(T0));
        hornet.Step(T0.AddSeconds(1));
        Assert.Equal("ok", Ack(hornet.TakePending(connected: true), "reboot").GetProperty("status").GetString());
        Assert.True(hornet.RebootRequested);
    }

    [Fact]
    public void Every_role_emits_contract_valid_messages_over_an_hour()
    {
        var specs = new[]
        {
            new HornetSpec { Id = "guard-gate1", Type = "guard-cam", Motion = new MotionSpec { EverySeconds = 30 } },
            new HornetSpec { Id = "meteo-out1", Type = "meteo", TeleIntervalS = 10 },
            new HornetSpec { Id = "heat-living1", Type = "heat", TeleIntervalS = 10, Heater = new HeaterSpec { StartC = 4, SensorFailAtS = 1800 } },
        };
        foreach (var spec in specs)
        {
            var hornet = new VirtualHornet(spec, HornetRole.Create(spec, "no-photos"), random: new Random(7));
            hornet.Start(T0);
            var all = hornet.OnConnected(T0).ToList();
            for (var t = T0; t < T0.AddHours(1); t = t.AddSeconds(1))
            {
                hornet.Step(t);
                all.AddRange(hornet.TakePending(connected: true));
            }
            Assert.True(all.Count > 20, $"{spec.Id} emitted only {all.Count} messages");
            AssertMatchesContract(all);
        }
    }
}
