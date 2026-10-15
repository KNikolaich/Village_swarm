using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Contracts;
using Hive.Contracts.Mqtt;
using Hive.Simulator;
using Hive.Simulator.Roles;

namespace Hive.Tests.Unit.Simulator;

/// <summary>cmd/wifi on the virtual hornet: acked, and the outcome of the trial reported as the firmware does.</summary>
public class WifiSwitchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static VirtualHornet Heat()
    {
        var spec = new HornetSpec { Id = "heat-living1", Type = "heat", Heater = new HeaterSpec { StartC = 15 } };
        var hornet = new VirtualHornet(spec, new HeatRole(spec.Heater, 60), random: new Random(1));
        hornet.Start(T0);
        return hornet;
    }

    private static List<Outgoing> Switch(string ssid, string pass)
    {
        var hornet = Heat();
        var now = T0.AddSeconds(5);
        var command = new JsonObject
        {
            ["v"] = 1, ["cid"] = Ulid.New(now), ["ts"] = now.ToUnixTimeMilliseconds(), ["ttl_s"] = 120, ["by"] = "user:test",
            ["ssid"] = ssid, ["pass"] = pass,
        };
        hornet.Receive(Topics.Cmd("heat-living1", "wifi"), command.ToJsonString());
        hornet.Step(now);
        return hornet.TakePending(connected: true).ToList();
    }

    private static JsonElement Json(IEnumerable<Outgoing> messages, string topicEnd) =>
        JsonDocument.Parse(messages.Single(m => m.Topic.EndsWith(topicEnd, StringComparison.Ordinal)).Payload).RootElement;

    private static void AssertMatchesContract(IEnumerable<Outgoing> messages)
    {
        foreach (var m in messages)
        {
            var schema = ContractSchemas.ForTopic(m.Topic);
            if (schema is null)
                continue;
            var result = ContractSchemas.Evaluate(schema, JsonNode.Parse(m.Payload));
            Assert.True(result.IsValid, $"{m.Topic} vs {schema}: {ContractSchemas.Describe(result)}");
        }
    }

    [Fact]
    public void A_wifi_switch_is_acked_and_reported_as_applied()
    {
        var messages = Switch("village-iot", "correct horse battery");

        Assert.Equal("ok", Json(messages, "/cmd/wifi/ack").GetProperty("status").GetString());
        var done = Json(messages, "/event/wifi");
        Assert.Equal(("applied", "village-iot"), (done.GetProperty("phase").GetString(), done.GetProperty("ssid").GetString()));
        AssertMatchesContract(messages);
    }

    [Fact]
    public void A_wifi_network_that_cannot_reach_the_hive_is_rolled_back()
    {
        var messages = Switch("guest-unreachable", "");

        var done = Json(messages, "/event/wifi");
        Assert.Equal("rolled_back", done.GetProperty("phase").GetString());
        Assert.Equal("warn", done.GetProperty("severity").GetString());
        AssertMatchesContract(messages);
    }

    [Fact]
    public void A_wifi_command_with_a_short_password_is_refused()
    {
        var messages = Switch("village-iot", "short");

        Assert.Equal("error", Json(messages, "/cmd/wifi/ack").GetProperty("status").GetString());
        Assert.DoesNotContain(messages, m => m.Topic.EndsWith("/event/wifi", StringComparison.Ordinal));
    }
}
