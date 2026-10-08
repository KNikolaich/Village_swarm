using System.Text;
using Hive.Domain;
using Hive.Modules.Telemetry.Ingest;

namespace Hive.Tests.Unit.Ingest;

public class NativeHornetAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static IngestItem? Parse(string topic, string payload) =>
        NativeHornetAdapter.Parse(topic, Encoding.UTF8.GetBytes(payload), Now, out _);

    private static string Example(string path) => File.ReadAllText(Path.Combine(ContractSchemas.ExamplesDir, path));

    [Fact]
    public void Parses_status()
    {
        Assert.Equal(new DeviceStatusChanged("guard-gate1", false, Now), Parse("vs/v1/dev/guard-gate1/status", "offline"));
        Assert.Null(NativeHornetAdapter.Parse("vs/v1/dev/guard-gate1/status", "sleepy"u8, Now, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Parses_info_example()
    {
        var info = Assert.IsType<DeviceInfoReceived>(Parse("vs/v1/dev/guard-gate1/info", Example("info/guard-cam.json")));
        Assert.Equal(("guard-cam", "0.3.2+a1b2c3d", "A0:B7:65:12:34:56", 17), (info.TypeCode, info.Fw, info.Mac, info.Boot));
        Assert.Equal(4, info.Info.RootElement.GetProperty("caps").GetArrayLength());
    }

    [Fact]
    public void Parses_tele_and_batch()
    {
        var single = Assert.IsType<TelemetryReceived>(Parse("vs/v1/dev/meteo-out1/tele/temperature", Example("tele/temperature.json")));
        Assert.Equal([("temperature", -3.42)], single.Values);

        var batch = Assert.IsType<TelemetryReceived>(Parse("vs/v1/dev/meteo-out1/tele/batch", Example("tele-batch/after-deep-sleep.json")));
        Assert.Equal([("temperature", -3.4), ("humidity", 81.0)], batch.Values);
    }

    [Fact]
    public void Parses_motion_event_with_payload_and_alarm_severity()
    {
        var e = Assert.IsType<DeviceEventReceived>(Parse("vs/v1/dev/guard-gate1/event/motion", Example("event-motion/armed-with-photos.json")));
        Assert.Equal(("motion", EventSeverity.Alarm, true), (e.Type, e.Severity, e.Armed));
        Assert.Equal("01J9Z3K7Q8M4N5P6R7S8T9V130", e.MessageId);
        Assert.Equal(3, e.Payload.RootElement.GetProperty("photos").GetArrayLength());
    }

    [Fact]
    public void Ignores_commands_acks_and_config_and_reports_garbage()
    {
        Assert.Null(Parse("vs/v1/dev/guard-gate1/cmd/arm", "{}"));
        Assert.Null(Parse("vs/v1/dev/guard-gate1/cmd/arm/ack", Example("ack/relay-ok.json")));
        Assert.Null(Parse("vs/v1/dev/guard-gate1/config", Example("config/guard-gate1.json")));

        Assert.Null(NativeHornetAdapter.Parse("vs/v1/dev/guard-gate1/health", "{not json"u8, Now, out var error));
        Assert.StartsWith("invalid payload", error);
        Assert.Null(NativeHornetAdapter.Parse("vs/v1/dev/guard-gate1/tele/temperature", """{"v":1}"""u8, Now, out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Effective_ts_trusts_device_clock_including_old_buffered_events()
    {
        var tenMinutesAgo = Now.AddMinutes(-10);
        Assert.Equal(tenMinutesAgo, NativeHornetAdapter.EffectiveTs(tenMinutesAgo.ToUnixTimeMilliseconds(), Now));
    }

    [Theory]
    [InlineData(0L)]                 // clock not synced yet
    [InlineData(1_000L)]             // 1970
    public void Effective_ts_falls_back_to_receive_time(long ts) =>
        Assert.Equal(Now, NativeHornetAdapter.EffectiveTs(ts, Now));

    [Fact]
    public void Effective_ts_rejects_clock_in_the_future()
    {
        Assert.Equal(Now, NativeHornetAdapter.EffectiveTs(Now.AddMinutes(6).ToUnixTimeMilliseconds(), Now));
        var slightlyAhead = Now.AddMinutes(1);
        Assert.Equal(slightlyAhead, NativeHornetAdapter.EffectiveTs(slightlyAhead.ToUnixTimeMilliseconds(), Now));
    }

    [Fact]
    public void Health_flags_clock_skew_in_both_directions()
    {
        Assert.True(NativeHornetAdapter.IsClockSkewed(0, Now));
        Assert.True(NativeHornetAdapter.IsClockSkewed(Now.AddMinutes(-6).ToUnixTimeMilliseconds(), Now));
        Assert.False(NativeHornetAdapter.IsClockSkewed(Now.AddSeconds(-3).ToUnixTimeMilliseconds(), Now));
    }

    [Fact]
    public void Recent_ids_forget_oldest_beyond_capacity()
    {
        var ids = new RecentIds(2);
        ids.Add("a");
        ids.Add("b");
        ids.Add("c");
        Assert.False(ids.Contains("a"));
        Assert.True(ids.Contains("b") && ids.Contains("c"));
    }
}
