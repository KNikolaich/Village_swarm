using Hive.Domain;
using Hive.Infrastructure;
using Hive.Modules.Cluster;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.Extensions.Options;

namespace Hive.Tests.Unit.Cluster;

/// <summary>Each node shows the other one's board readings as telemetry of device hive-{node}.</summary>
public class NodeStatsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static (NodeStatsReceiver Receiver, IngestQueue Queue) Make(string node)
    {
        var queue = new IngestQueue();
        var receiver = new NodeStatsReceiver(
            Options.Create(new ClusterOptions { Enabled = true, StatsEveryS = 60 }),
            Options.Create(new HiveOptions { Node = node }), queue, TimeProvider.System);
        return (receiver, queue);
    }

    private static List<IngestItem> Drain(IngestQueue queue)
    {
        var items = new List<IngestItem>();
        while (queue.Reader.TryRead(out var item))
            items.Add(item);
        return items;
    }

    private static NodeStatsMessage Stats(string node, DateTimeOffset at) =>
        new(1, at.ToUnixTimeMilliseconds(), node, new Dictionary<string, double> { ["cpu_temperature"] = 48.5, ["mem_used_mb"] = 700 });

    [Fact]
    public void Home_makes_the_hive_device_of_the_city_and_shows_its_readings()
    {
        var (receiver, queue) = Make("home");

        receiver.Accept(Stats("city", T0), T0.AddSeconds(1));

        var items = Drain(queue);
        Assert.Equal(["hive-city"], items.Select(i => i.DeviceId).Distinct());
        Assert.Contains(items, i => i is DeviceInfoReceived { TypeCode: "hive" });
        var telemetry = Assert.Single(items.OfType<TelemetryReceived>());
        Assert.Contains(("cpu_temperature", 48.5), telemetry.Values);
        Assert.Contains(items, i => i is DeviceStatusChanged { Online: true });
    }

    [Fact]
    public void The_city_only_adds_readings_to_the_device_the_watch_on_home_keeps()
    {
        var (receiver, queue) = Make("city");

        receiver.Accept(Stats("home", T0), T0.AddSeconds(1));

        var items = Drain(queue);
        var telemetry = Assert.Single(items);
        Assert.IsType<TelemetryReceived>(telemetry);
        Assert.Equal("hive-home", telemetry.DeviceId);
    }

    [Fact]
    public void Silence_marks_the_city_offline_once_and_the_next_message_brings_it_back()
    {
        var (receiver, queue) = Make("home");
        receiver.Accept(Stats("city", T0), T0);
        Drain(queue);

        receiver.CheckSilence(T0.AddSeconds(100));
        Assert.Empty(Drain(queue));

        receiver.CheckSilence(T0.AddSeconds(200));
        receiver.CheckSilence(T0.AddSeconds(260));
        Assert.Single(Drain(queue), i => i is DeviceStatusChanged { Online: false });

        receiver.Accept(Stats("city", T0.AddSeconds(300)), T0.AddSeconds(300));
        Assert.Contains(Drain(queue), i => i is DeviceStatusChanged { Online: true });
    }

    [Fact]
    public void The_other_hive_alias_is_kept_and_an_empty_one_means_none()
    {
        var (receiver, _) = Make("home");

        receiver.Accept(Stats("city", T0) with { Alias = "Sbrbn" }, T0);
        Assert.Equal("Sbrbn", receiver.OtherAlias);

        receiver.Accept(Stats("city", T0) with { Alias = " " }, T0);
        Assert.Null(receiver.OtherAlias);
    }

    [Fact]
    public void The_city_saying_it_keeps_the_notes_is_remembered_and_not_saying_it_means_no()
    {
        var (receiver, _) = Make("home");
        Assert.False(receiver.NotesMasterDeclared);

        receiver.Accept(Stats("city", T0) with { Caps = [NotesMasterPolicy.Cap] }, T0);
        Assert.True(receiver.NotesMasterDeclared);

        receiver.Accept(Stats("city", T0.AddMinutes(1)), T0.AddMinutes(1));   // a city that was not updated
        Assert.False(receiver.NotesMasterDeclared);
    }

    [Fact]
    public void An_old_retained_message_does_not_bring_the_other_node_online()
    {
        var (receiver, queue) = Make("home");

        receiver.Accept(Stats("city", T0), T0.AddHours(3));

        Assert.DoesNotContain(Drain(queue), i => i is DeviceStatusChanged);
    }
}
