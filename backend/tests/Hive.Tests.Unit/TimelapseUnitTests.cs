using System.Text.Json;
using Hive.Domain;
using Hive.Modules.Devices;

namespace Hive.Tests.Unit;

/// <summary>Timelapse pace: slots counted from the start, one quick retry after a failure, which relay lights the camera.</summary>
public sealed class TimelapseUnitTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static TimelapseState State(int intervalMin, int failures = 0) =>
        new(intervalMin, true, Start, "user:test", Start, Failures: failures);

    [Theory]
    [InlineData(30, 0, 30)]
    [InlineData(30, 29, 30)]
    [InlineData(30, 30, 60)]
    [InlineData(90, 200, 270)] // missed slots are skipped, the pace stays
    [InlineData(1440, 5, 1440)]
    public void The_next_slot_counts_whole_intervals_from_the_start(int interval, int nowMin, int slotMin) =>
        Assert.Equal(Start.AddMinutes(slotMin), Timelapse.NextSlot(State(interval), Start.AddMinutes(nowMin)));

    [Fact]
    public void A_first_failure_retries_in_a_minute_then_the_pace_takes_over()
    {
        var now = Start.AddSeconds(95);
        var first = Timelapse.Failed(State(60), now, "no_photo");
        Assert.Equal(now + Timelapse.RetryAfter, first.NextAt);
        Assert.Equal(1, first.Failures);
        Assert.Null(first.Shot);

        var second = Timelapse.Failed(first, now.AddMinutes(1), "no_photo");
        Assert.Equal(Start.AddMinutes(60), second.NextAt);
        Assert.Equal(2, second.Failures);

        var done = Timelapse.Done(second, Start.AddMinutes(60).AddSeconds(8), "01J0000000000000000000000-0");
        Assert.Equal(0, done.Failures);
        Assert.Equal(Start.AddMinutes(120), done.NextAt);
        Assert.Equal("no_photo", done.LastError); // the last failure stays on record, with its time
    }

    [Fact]
    public void A_retry_never_goes_past_the_next_slot()
    {
        // A 5-minute timelapse failing 4.5 minutes in: the next slot comes before a retry would.
        var failed = Timelapse.Failed(State(5), Start.AddMinutes(4.5), "offline");
        Assert.Equal(Start.AddMinutes(5), failed.NextAt);
    }

    [Fact]
    public void The_light_is_the_configured_relay_else_the_reported_one_else_relay()
    {
        var d = new Device { DeviceId = "cam", TypeCode = "guard-cam", Name = "cam" };
        Assert.Equal("relay", Timelapse.LightChannel(d));
        d.State = JsonDocument.Parse("""{"armed":true,"flood":"off"}""");
        Assert.Equal("flood", Timelapse.LightChannel(d));
        Assert.False(Timelapse.IsOn(d, "flood"));
        d.Config = JsonDocument.Parse("""{"relay":{"channel":"lamp"}}""");
        Assert.Equal("lamp", Timelapse.LightChannel(d));
        d.State = JsonDocument.Parse("""{"lamp":"on"}""");
        Assert.True(Timelapse.IsOn(d, "lamp"));
    }

    [Fact]
    public void The_light_goes_on_with_its_own_time_limit()
    {
        var on = Timelapse.LightOn("relay");
        Assert.Equal(Timelapse.LightForS, on["for_s"]!.GetValue<int>());
        Assert.True(TimeSpan.FromSeconds(Timelapse.LightForS) > Timelapse.LightWarmup + TimelapseWatch.Tick + Timelapse.PhotoTimeout);
        Assert.Equal("off", Timelapse.LightOff("relay")["set"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(90, true)]
    [InlineData(1440, true)]
    [InlineData(7, false)]
    [InlineData(0, false)]
    public void Only_the_offered_intervals_are_accepted(int interval, bool ok) =>
        Assert.Equal(ok, TimelapseService.Validate(new SetTimelapseRequest(interval)) is null);
}
