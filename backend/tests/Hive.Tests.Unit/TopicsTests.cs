using Hive.Contracts.Mqtt;

namespace Hive.Tests.Unit;

public class TopicsTests
{
    [Theory]
    [InlineData("guard-gate1")]
    [InlineData("meteo-out1")]
    [InlineData("heat-living1")]
    [InlineData("a")]
    public void Valid_device_ids_are_accepted(string id) => Assert.True(Topics.IsValidDeviceId(id));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Guard-gate1")]
    [InlineData("1guard")]
    [InlineData("guard--gate")]
    [InlineData("guard-")]
    [InlineData("guard gate")]
    [InlineData("ворота")]
    [InlineData("guard_gate1")]
    [InlineData("abcdefghijklmnopqrstuvwxyz1234567")] // 33 chars
    public void Invalid_device_ids_are_rejected(string? id) => Assert.False(Topics.IsValidDeviceId(id));

    [Fact]
    public void Builds_device_topics()
    {
        Assert.Equal("vs/v1/dev/guard-gate1/status", Topics.Status("guard-gate1"));
        Assert.Equal("vs/v1/dev/guard-gate1/info", Topics.Info("guard-gate1"));
        Assert.Equal("vs/v1/dev/guard-gate1/health", Topics.Health("guard-gate1"));
        Assert.Equal("vs/v1/dev/guard-gate1/state", Topics.State("guard-gate1"));
        Assert.Equal("vs/v1/dev/guard-gate1/log", Topics.Log("guard-gate1"));
        Assert.Equal("vs/v1/dev/meteo-out1/tele/temperature", Topics.Tele("meteo-out1", "temperature"));
        Assert.Equal("vs/v1/dev/guard-gate1/event/motion", Topics.Event("guard-gate1", "motion"));
        Assert.Equal("vs/v1/dev/guard-gate1/cmd/snapshot", Topics.Cmd("guard-gate1", "snapshot"));
        Assert.Equal("vs/v1/dev/guard-gate1/cmd/snapshot/ack", Topics.CmdAck("guard-gate1", "snapshot"));
        Assert.Equal("vs/v1/dev/guard-gate1/config", Topics.Config("guard-gate1"));
    }

    [Fact]
    public void Builds_hive_topics()
    {
        Assert.Equal("vs/v1/hive/home/heartbeat", Topics.HiveHeartbeat("home"));
        Assert.Equal("vs/v1/hive/active", Topics.HiveActive);
        Assert.Equal("vs/v1/hive/broadcast/cmd/time_sync", Topics.Broadcast("time_sync"));
        Assert.Equal("vs/v1/dev/+/#", Topics.AllDevices);
    }

    [Fact]
    public void Rejects_bad_ids_and_segments()
    {
        Assert.Throws<ArgumentException>(() => Topics.Status("Bad Id"));
        Assert.Throws<ArgumentException>(() => Topics.Tele("guard-gate1", "temp/x"));
        Assert.Throws<ArgumentException>(() => Topics.Event("guard-gate1", "#"));
    }

    [Theory]
    [InlineData("vs/v1/dev/guard-gate1/event/motion", "guard-gate1")]
    [InlineData("vs/v1/dev/meteo-out1/tele/temperature", "meteo-out1")]
    [InlineData("vs/v1/dev/guard-gate1", null)]
    [InlineData("vs/v1/hive/home/heartbeat", null)]
    [InlineData("vs/v2/dev/guard-gate1/status", null)]
    [InlineData("vs/v1/dev/BAD/status", null)]
    public void Extracts_device_id(string topic, string? expected) =>
        Assert.Equal(expected, Topics.TryGetDeviceId(topic));
}
