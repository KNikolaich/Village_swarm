using Hive.Domain;
using Hive.Infrastructure;
using Hive.Modules.Cluster;
using Microsoft.Extensions.Options;

namespace Hive.Tests.Unit.Cluster;

/// <summary>The dead man's switch of the city (spec 11.4) and what a node may do in each role (spec 11.3).</summary>
public class ClusterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static HomeWatchLogic Logic() => new(T0, TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(900));

    [Fact]
    public void A_heartbeat_every_half_minute_keeps_it_quiet()
    {
        var logic = Logic();
        for (var s = 0; s <= 3600; s += 30)
        {
            Assert.Null(logic.OnHeartbeat(T0.AddSeconds(s)));
            Assert.Empty(logic.OnTick(T0.AddSeconds(s + 10)));
        }
        Assert.Equal(HomePhase.Up, logic.Phase);
    }

    [Fact]
    public void Three_minutes_of_silence_warn_and_a_quarter_of_an_hour_alarm_each_once()
    {
        var logic = Logic();
        logic.OnHeartbeat(T0);
        Assert.Empty(logic.OnTick(T0.AddSeconds(179)));
        Assert.Equal([HomeChange.Silent], logic.OnTick(T0.AddSeconds(180)));
        Assert.Empty(logic.OnTick(T0.AddSeconds(200)));        // not again
        Assert.Empty(logic.OnTick(T0.AddSeconds(899)));
        Assert.Equal([HomeChange.SilentLong], logic.OnTick(T0.AddSeconds(900)));
        Assert.Empty(logic.OnTick(T0.AddSeconds(5000)));
        Assert.Equal(HomePhase.SilentLong, logic.Phase);
    }

    [Fact]
    public void A_city_that_has_just_started_does_not_alarm_before_home_had_its_chance()
    {
        var logic = Logic();   // no heartbeat yet: silence counts from the start of the watch
        Assert.Empty(logic.OnTick(T0.AddSeconds(100)));
        Assert.Equal([HomeChange.Silent], logic.OnTick(T0.AddSeconds(181)));
    }

    [Fact]
    public void A_long_gap_between_rounds_gives_both_steps_at_once_and_a_heartbeat_ends_it()
    {
        var logic = Logic();
        logic.OnHeartbeat(T0);
        Assert.Equal([HomeChange.Silent, HomeChange.SilentLong], logic.OnTick(T0.AddHours(1)));
        Assert.Equal(HomeChange.Back, logic.OnHeartbeat(T0.AddHours(1).AddSeconds(5)));
        Assert.Equal(HomePhase.Up, logic.Phase);
        Assert.Null(logic.OnHeartbeat(T0.AddHours(1).AddSeconds(35)));
        Assert.Equal(TimeSpan.FromSeconds(10), logic.SilentFor(T0.AddHours(1).AddSeconds(45)));
    }

    private static NodeState State(string node, string role) => new(Options.Create(new HiveOptions { Node = node, Role = role }));

    [Fact]
    public void The_home_node_is_active_and_a_city_observer_holds_back()
    {
        var home = State("home", "active");
        Assert.Equal(NodeRole.Active, home.Role);
        Assert.True(home.RulesRun);
        Assert.True(home.RulesMayCommand);
        Assert.False(home.ReadOnly);
        Assert.True(home.Notifies("guard-gate1"));

        var city = State("city", "observer");
        Assert.False(city.RulesRun);
        Assert.False(city.RulesMayCommand);
        Assert.True(city.ReadOnly);
        // an observer tells about home, and about nothing that home already tells
        Assert.True(city.Notifies("hive-home"));
        Assert.False(city.Notifies("guard-gate1"));
        Assert.False(city.Notifies("hive-city"));
    }

    [Fact]
    public void A_standby_city_notifies_about_everything_and_its_rules_only_notify()
    {
        var city = State("city", "observer");
        city.Set(NodeRole.Standby);
        Assert.True(city.RulesRun);
        Assert.False(city.RulesMayCommand);   // a rule must not command devices home may be commanding too
        Assert.False(city.ReadOnly);           // a person may still send a command by hand
        Assert.True(city.Notifies("guard-gate1"));
    }
}

public sealed class ReplicaCodecTests
{
    [Fact]
    public void A_chunk_survives_packing_and_a_request_is_plain_json()
    {
        var chunk = new ReplicaChunk(1, "req-1", 0, 1, false, 42,
            [new ReplicaEvent("e1", "guard-1", "motion", "Alarm", 1000, 1001, System.Text.Json.JsonDocument.Parse("{\"zone\":2}").RootElement, true)]);
        var back = ReplicaCodec.UnpackChunk(ReplicaCodec.PackChunk(chunk))!;
        Assert.Equal(("req-1", 42L), (back.RequestId, back.LastTs));
        Assert.Equal(("e1", "guard-1", true), (back.Events[0].Id, back.Events[0].DeviceId, back.Events[0].Armed));
        Assert.Equal(2, back.Events[0].Payload.GetProperty("zone").GetInt32());

        var request = ReplicaCodec.UnpackRequest(System.Text.Encoding.UTF8.GetBytes(ReplicaCodec.PackRequest(new ReplicaRequest(1, "r", 5))))!;
        Assert.Equal(("r", 5L), (request.Id, request.Since));
    }
}
