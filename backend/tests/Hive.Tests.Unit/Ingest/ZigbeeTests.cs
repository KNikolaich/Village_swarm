using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Domain;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hive.Tests.Unit.Ingest;

/// <summary>zigbee2mqtt (contracts/zigbee.md): its reports become ordinary ingest items, our commands become its /set.</summary>
public class ZigbeeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly ZigbeeCatalog _catalog = new();
    private readonly TasmotaPending _pending = new(new ManualTime(Now));
    private readonly ZigbeeAdapter _adapter;

    public ZigbeeTests() =>
        _adapter = new ZigbeeAdapter(new IngestQueue(), _catalog, _pending, Options.Create(new ZigbeeOptions { Enabled = true }), NullLogger<ZigbeeAdapter>.Instance);

    private IReadOnlyList<IngestItem> Handle(string topic, string payload) => _adapter.Handle(topic, Encoding.UTF8.GetBytes(payload), Now);

    [Fact]
    public void A_sensor_report_gives_telemetry_and_marks_it_online()
    {
        var items = Handle("door-gate1", """{"temperature":21.5,"humidity":44.2,"battery":87,"voltage":2950,"linkquality":120}""");

        Assert.Equal(new DeviceStatusChanged("door-gate1", true, Now), items[0]);
        var values = Assert.IsType<TelemetryReceived>(items[1]).Values.ToDictionary(v => v.Metric, v => v.Value);
        Assert.Equal((21.5, 44.2, 87.0, 2.95, 120.0), (values["temperature"], values["humidity"], values["battery_pct"], values["battery_voltage"], values["linkquality"]));
    }

    [Fact]
    public void Voltage_of_a_plug_is_volts_and_of_a_battery_sensor_millivolts()
    {
        var plug = Assert.IsType<TelemetryReceived>(Handle("plug-yard1", """{"voltage":229.5,"power":1480,"current":6.4,"energy":12.5}""")[1]).Values.ToDictionary(v => v.Metric, v => v.Value);
        Assert.Equal((229.5, 1480.0, 6.4, 12.5), (plug["voltage_v"], plug["power_w"], plug["current_a"], plug["energy_kwh"]));
        Assert.False(plug.ContainsKey("battery_voltage"));
    }

    [Fact]
    public void A_plug_state_updates_the_relay_and_closes_the_waiting_command()
    {
        _pending.Expect("plug-yard1", "power", "01CID");
        var items = Handle("plug-yard1", """{"state":"ON","linkquality":90}""");

        var state = Assert.IsType<DeviceStateReceived>(items.First(i => i is DeviceStateReceived));
        Assert.True(state.Merge);
        Assert.Equal("on", state.State.RootElement.GetProperty("relay").GetString());
        Assert.Equal("01CID", Assert.IsType<CommandAckReceived>(items.Last()).Cid);
    }

    [Fact]
    public void Several_gangs_are_numbered()
    {
        var state = Assert.IsType<DeviceStateReceived>(Handle("relay-hall2", """{"state_l1":"ON","state_l2":"OFF"}""").First(i => i is DeviceStateReceived));
        Assert.Equal("on", state.State.RootElement.GetProperty("relay1").GetString());
        Assert.Equal("off", state.State.RootElement.GetProperty("relay2").GetString());
    }

    [Fact]
    public void Motion_is_an_event_once_per_change_not_on_every_report()
    {
        Assert.Equal("motion", Assert.IsType<DeviceEventReceived>(Handle("pir-hall1", """{"occupancy":true}""").Last()).Type);
        Assert.DoesNotContain(Handle("pir-hall1", """{"occupancy":true,"illuminance_lux":12}"""), i => i is DeviceEventReceived);
        Assert.DoesNotContain(Handle("pir-hall1", """{"occupancy":false}"""), i => i is DeviceEventReceived);
        Assert.Contains(Handle("pir-hall1", """{"occupancy":true}"""), i => i is DeviceEventReceived);
    }

    [Fact]
    public void A_door_that_opens_and_a_leak_are_events_with_their_severity()
    {
        Assert.DoesNotContain(Handle("door-gate1", """{"contact":true}"""), i => i is DeviceEventReceived);
        var opened = Assert.IsType<DeviceEventReceived>(Handle("door-gate1", """{"contact":false}""").Last());
        Assert.Equal(("door_open", EventSeverity.Info), (opened.Type, opened.Severity));
        var leak = Assert.IsType<DeviceEventReceived>(Handle("leak-bath1", """{"water_leak":true}""").Last());
        Assert.Equal(("leak", EventSeverity.Critical), (leak.Type, leak.Severity));
    }

    [Fact]
    public void Availability_is_the_online_status_in_both_formats()
    {
        Assert.Equal([new DeviceStatusChanged("door-gate1", false, Now)], Handle("door-gate1/availability", """{"state":"offline"}"""));
        Assert.Equal([new DeviceStatusChanged("door-gate1", true, Now)], Handle("door-gate1/availability", "online"));
    }

    [Fact]
    public void The_bridge_state_and_its_device_list_fill_the_catalog()
    {
        Assert.Empty(Handle("bridge/state", """{"state":"online"}"""));
        Assert.Equal("online", _catalog.Bridge);
        Handle("bridge/state", "offline");
        Assert.Equal("offline", _catalog.Bridge);

        Handle("bridge/devices", """
            [{"ieee_address":"0x00124b0000000001","type":"Coordinator","friendly_name":"Coordinator"},
             {"ieee_address":"0x00158d0001a2b3c4","friendly_name":"0x00158d0001a2b3c4","type":"EndDevice","supported":true,"interview_completed":true,
              "definition":{"model":"RTCGQ11LM","vendor":"Aqara","description":"Motion sensor"}},
             {"ieee_address":"0x00158d0001a2b3c5","friendly_name":"new","type":"EndDevice","supported":false,"interview_completed":false,"definition":null}]
            """);
        var devices = _catalog.Devices;
        Assert.Equal(2, devices.Count);   // the coordinator is not a device
        Assert.Equal(("RTCGQ11LM", "Aqara", true), (devices[0].Model, devices[0].Vendor, devices[0].InterviewCompleted));
        Assert.Null(devices[1].Model);
    }

    [Fact]
    public void Topics_that_are_not_devices_are_ignored()
    {
        Assert.Empty(Handle("0x00158d0001a2b3c4", """{"temperature":20}"""));      // not a valid device id: not added yet
        Assert.Empty(Handle("door-gate1/set", """{"state":"ON"}"""));
        Assert.Empty(Handle("bridge/event", """{"type":"device_joined"}"""));
        Assert.Empty(Handle("door-gate1", "[1,2]"));
    }

    [Fact]
    public void Commands_become_state_requests_and_timed_on_is_refused()
    {
        static ZigbeePlan? Plan(string name, string? json = null) => ZigbeeCommands.Plan(name, json is null ? null : JsonNode.Parse(json)!.AsObject());
        Assert.Equal("""{"state":"ON"}""", Plan("relay", """{"channel":"relay","set":"on"}""")!.Call.Payload);
        Assert.Equal("""{"state":"OFF"}""", Plan("relay", """{"set":"off"}""")!.Call.Payload);
        Assert.Equal("""{"state":"TOGGLE"}""", Plan("relay", """{"set":"toggle"}""")!.Call.Payload);
        Assert.Null(Plan("relay", """{"set":"on","for_s":3600}"""));   // nothing would switch it off if the hive were gone
        Assert.Null(Plan("reboot"));
        Assert.Null(Plan("relay", """{"set":"dim"}"""));
    }
}
