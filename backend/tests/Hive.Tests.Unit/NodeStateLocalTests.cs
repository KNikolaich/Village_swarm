using Hive.Infrastructure;
using Microsoft.Extensions.Options;

namespace Hive.Tests.Unit;

/// <summary>An observer node (the city) still runs the rules of, commands and tells about the hornets it made itself.</summary>
public class NodeStateLocalTests
{
    private static NodeState City() => new(Options.Create(new HiveOptions { Node = "city", Role = "observer" }));

    [Fact]
    public void An_observer_treats_its_own_devices_like_an_active_node_and_the_others_like_copies()
    {
        var node = City();
        node.MarkLocal("meteo-own1");
        Assert.True(node.IsLocal("meteo-own1"));
        Assert.False(node.IsLocal("meteo-copy1"));

        Assert.True(node.RulesRunFor("meteo-own1"));
        Assert.False(node.RulesRunFor("meteo-copy1"));
        Assert.False(node.RulesRunFor(null));
        Assert.True(node.MayCommand("meteo-own1"));
        Assert.False(node.MayCommand("meteo-copy1"));
        Assert.True(node.Notifies("meteo-own1"));
        Assert.False(node.Notifies("meteo-copy1"));
        Assert.True(node.Notifies("hive-home"));   // the other node itself is still spoken about
    }

    [Fact]
    public void The_active_node_needs_no_marks_and_the_set_can_be_loaded_and_changed()
    {
        var home = new NodeState(Options.Create(new HiveOptions { Node = "home", Role = "active" }));
        Assert.True(home.RulesRunFor("anything"));
        Assert.True(home.MayCommand("anything"));
        Assert.True(home.Notifies("anything"));

        var node = City();
        node.LoadLocal(["a-1", "b-2"]);
        Assert.True(node.IsLocal("a-1") && node.IsLocal("b-2"));
        node.ForgetLocal("a-1");
        Assert.False(node.IsLocal("a-1"));
    }
}
