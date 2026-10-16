using System.Text;
using System.Text.Json.Nodes;
using Hive.Domain;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Telemetry.Ingest;

namespace Hive.Tests.Unit.Ingest;

internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Tasmota and OpenBeken devices (contracts/tasmota.md): what they send becomes ordinary ingest items.</summary>
public class TasmotaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Now);
    private readonly TasmotaPending _pending;

    public TasmotaTests() => _pending = new TasmotaPending(_time);

    private IReadOnlyList<IngestItem> Parse(string topic, string payload) =>
        TasmotaAdapter.Parse(topic, Encoding.UTF8.GetBytes(payload), Now, _pending);

    [Fact]
    public void Last_will_is_the_online_status()
    {
        Assert.Equal([new DeviceStatusChanged("heat-bed1", true, Now)], Parse("tele/heat-bed1/LWT", "Online"));
        Assert.Equal([new DeviceStatusChanged("heat-bed1", false, Now)], Parse("tele/heat-bed1/LWT", "Offline"));
        Assert.Empty(Parse("tele/heat-bed1/LWT", "Sleeping"));
    }

    [Fact]
    public void State_gives_health_and_where_the_relay_is()
    {
        var items = Parse("tele/heat-bed1/STATE", """
            {"Time":"2026-10-16T15:00:00","Uptime":"0T00:05:12","UptimeSec":312,"Heap":26,"MqttCount":2,"POWER":"ON",
             "Wifi":{"AP":1,"SSId":"x","RSSI":58,"Signal":-71,"LinkCount":1}}
            """);

        var health = Assert.IsType<DeviceHealthReceived>(items[0]);
        Assert.Equal((312L, -71, 26L * 1024, 2), (health.UptimeS, health.Rssi, health.HeapFree, health.Boot));
        var state = Assert.IsType<DeviceStateReceived>(items[1]);
        Assert.True(state.Merge);
        Assert.Equal("on", state.State.RootElement.GetProperty("relay").GetString());
    }

    [Fact]
    public void Several_relays_are_numbered()
    {
        var items = Parse("tele/relay-yard1/STATE", """{"UptimeSec":5,"POWER1":"OFF","POWER2":"ON","Wifi":{"Signal":-50}}""");
        var state = Assert.IsType<DeviceStateReceived>(items[1]);
        Assert.Equal("off", state.State.RootElement.GetProperty("relay1").GetString());
        Assert.Equal("on", state.State.RootElement.GetProperty("relay2").GetString());
    }

    [Fact]
    public void Sensor_blocks_become_metrics()
    {
        var items = Parse("tele/heat-bed1/SENSOR", """
            {"Time":"2026-10-16T15:00:00","AM2301":{"Temperature":21.5,"Humidity":40.2,"DewPoint":7.5},"TempUnit":"C"}
            """);
        Assert.Equal([("temperature", 21.5), ("humidity", 40.2)], Assert.IsType<TelemetryReceived>(Assert.Single(items)).Values);
    }

    [Fact]
    public void A_second_thermometer_and_the_energy_meter_get_their_own_names()
    {
        var items = Parse("tele/heat-bed1/SENSOR", """
            {"DS18B20-1":{"Id":"A","Temperature":20.0},"DS18B20-2":{"Id":"B","Temperature":55.5},
             "ENERGY":{"Power":1500,"Voltage":228,"Current":6.6,"Today":3.2,"Total":120.5,"Factor":0.99}}
            """);
        var values = Assert.IsType<TelemetryReceived>(Assert.Single(items)).Values.ToDictionary(v => v.Metric, v => v.Value);
        Assert.Equal(20.0, values["temperature"]);
        Assert.Equal(55.5, values["temperature2"]);
        Assert.Equal(1500, values["power_w"]);
        Assert.Equal(228, values["voltage_v"]);
        Assert.Equal(120.5, values["energy_total_kwh"]);
        Assert.False(values.ContainsKey("factor"));
    }

    [Fact]
    public void A_switch_answer_updates_the_state_and_closes_the_waiting_command()
    {
        _pending.Expect("heat-bed1", "power", "01CID");
        var items = Parse("stat/heat-bed1/POWER", "ON");

        Assert.Equal("on", Assert.IsType<DeviceStateReceived>(items[0]).State.RootElement.GetProperty("relay").GetString());
        var ack = Assert.IsType<CommandAckReceived>(items[1]);
        Assert.Equal(("01CID", "ok"), (ack.Cid, ack.Status));

        // the same answer again closes nothing: the command was answered once
        Assert.Single(Parse("stat/heat-bed1/POWER", "ON"));
    }

    [Fact]
    public void An_answer_with_nothing_waiting_only_updates_the_state()
    {
        Assert.Single(Parse("stat/relay-yard1/POWER2", "OFF"));
        Assert.Equal("off", Assert.IsType<DeviceStateReceived>(Parse("stat/relay-yard1/POWER2", "OFF")[0]).State.RootElement.GetProperty("relay2").GetString());
    }

    [Fact]
    public void A_late_answer_does_not_close_an_old_command()
    {
        _pending.Expect("heat-bed1", "power", "OLD");
        _time.Advance(TimeSpan.FromMinutes(3));
        Assert.Single(Parse("stat/heat-bed1/POWER", "OFF"));
    }

    [Fact]
    public void Restart_is_confirmed_by_the_result()
    {
        _pending.Expect("heat-bed1", "restart", "01RESTART");
        var ack = Assert.IsType<CommandAckReceived>(Assert.Single(Parse("stat/heat-bed1/RESULT", """{"Restart":"Restarting"}""")));
        Assert.Equal("01RESTART", ack.Cid);
    }

    [Fact]
    public void Other_topics_and_bad_ids_are_ignored()
    {
        Assert.Empty(Parse("tele/heat-bed1/INFO1", """{"Module":"Sonoff Basic"}"""));
        Assert.Empty(Parse("tele/Bad_Id/LWT", "Online"));
        Assert.Empty(Parse("cmnd/heat-bed1/POWER", "ON"));
        Assert.Empty(Parse("stat/heat-bed1/POWERX", "ON"));
        Assert.Empty(Parse("stat/heat-bed1/POWER", "UNKNOWN"));
    }

    // --- hive commands to Tasmota ---

    private static TasmotaPlan? Plan(string name, string? json = null) =>
        TasmotaCommands.Plan(name, json is null ? null : JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void Relay_commands_map_to_power()
    {
        Assert.Equal(new TasmotaCall("POWER", "OFF"), Plan("relay", """{"channel":"heater","set":"off"}""")!.Calls[0]);
        Assert.Equal(new TasmotaCall("POWER", "ON"), Plan("relay", """{"channel":"relay","set":"on"}""")!.Calls[0]);
        Assert.Equal(new TasmotaCall("POWER2", "TOGGLE"), Plan("relay", """{"channel":"relay2","set":"toggle"}""")!.Calls[0]);
        Assert.Equal("power", Plan("relay", """{"set":"on"}""")!.Confirms);
    }

    [Fact]
    public void Timed_on_is_a_pulse_time_so_the_relay_switches_itself_off()
    {
        // above 111 the value is seconds plus 100; up to 11 seconds it is tenths of a second
        Assert.Equal("PulseTime1 3700; POWER ON", Plan("relay", """{"set":"on","for_s":3600}""")!.Calls[0].Payload);
        Assert.Equal("PulseTime1 50; POWER ON", Plan("relay", """{"set":"on","for_s":5}""")!.Calls[0].Payload);
        Assert.Equal("PulseTime2 3700; POWER2 ON", Plan("relay", """{"channel":"relay2","set":"on","for_s":3600}""")!.Calls[0].Payload);
        Assert.Equal("PulseTime1 64900; POWER ON", Plan("relay", """{"set":"on","for_s":99999}""")!.Calls[0].Payload);
    }

    [Fact]
    public void Reboot_is_restart_and_the_rest_is_refused()
    {
        Assert.Equal(new TasmotaCall("Restart", "1"), Plan("reboot")!.Calls[0]);
        Assert.Null(Plan("snapshot"));
        Assert.Null(Plan("wifi", """{"ssid":"x","pass":"12345678"}"""));
        Assert.Null(Plan("relay", """{"set":"dim"}"""));
        Assert.Null(Plan("relay"));
    }

    [Fact]
    public void Topics_split_into_area_id_and_kind()
    {
        Assert.Equal(("tele", "heat-bed1", "STATE"), TasmotaTopics.TryParse("tele/heat-bed1/STATE"));
        Assert.Null(TasmotaTopics.TryParse("tele/heat-bed1/a/b"));
        Assert.Null(TasmotaTopics.TryParse("vs/v1/dev/x/status"));
        Assert.Equal("cmnd/heat-bed1/POWER", TasmotaTopics.Cmnd("heat-bed1", "POWER"));
    }
}
