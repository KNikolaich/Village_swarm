using System.Text.Json;
using Hive.Domain;
using Hive.Modules.Notify;
using Hive.Modules.Telemetry;

namespace Hive.Tests.Unit;

/// <summary>Monitoring mode: when a limit rings and clears, what chart buckets short windows get, what Telegram says.</summary>
public sealed class MonitoringUnitTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static MonitorState State(double? below, double? above, string? breach = null) =>
        new(Now, Now.AddHours(2), 5, "temperature", below, above, null, "user:test", breach);

    [Theory]
    [InlineData(120, null, null)]
    [InlineData(109.9, "below", "monitor_threshold")]
    [InlineData(130.1, "above", "monitor_threshold")]
    [InlineData(110, null, null)] // on the line is still inside
    public void A_reading_inside_or_outside_the_limits(double value, string? breach, string? type)
    {
        var (after, events) = MonitoringLimits.Step(State(110, 130), value);
        Assert.Equal(breach, after);
        Assert.Equal(type, events.SingleOrDefault().Type);
        if (type is not null)
            Assert.Equal(EventSeverity.Alarm, events.Single().Severity);
    }

    [Fact]
    public void A_crossed_limit_clears_only_past_the_hysteresis_and_rings_once()
    {
        Assert.Empty(MonitoringLimits.Step(State(110, 130, "below"), 105).Events);
        var (stay, none) = MonitoringLimits.Step(State(110, 130, "below"), 110.3);
        Assert.Equal("below", stay);
        Assert.Empty(none);
        var (clear, events) = MonitoringLimits.Step(State(110, 130, "below"), 110.5);
        Assert.Null(clear);
        Assert.Equal([("monitor_normal", EventSeverity.Info, 110.0)], events);
        // Straight from too cold to too hot is a new alarm.
        Assert.Equal("above", MonitoringLimits.Step(State(110, 130, "below"), 140).Breach);
    }

    [Fact]
    public void Without_limits_nothing_rings()
    {
        var (breach, events) = MonitoringLimits.Step(State(null, null), -40);
        Assert.Null(breach);
        Assert.Empty(events);
        Assert.Equal("above", MonitoringLimits.Step(State(null, 90), 95).Breach);
    }

    [Theory]
    [InlineData(1, 360, 10)]
    [InlineData(0.5, 360, 5)]
    [InlineData(2, 360, 20)]
    [InlineData(6, 360, 60)]
    [InlineData(24, 120, 720)]
    public void Short_windows_get_buckets_of_a_few_seconds(double hours, int points, double bucketS)
    {
        var queries = new TelemetryQueries(null!, TimeProvider.System);
        var (_, _, bucket) = queries.Window(Now.AddHours(-hours), Now, points);
        Assert.Equal(bucketS, bucket);
    }

    [Fact]
    public void Silence_counts_three_intervals_and_never_less_than_twenty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), MonitoringWatch.StaleAfter(5));
        Assert.Equal(TimeSpan.FromSeconds(30), MonitoringWatch.StaleAfter(10));
        Assert.Equal(TimeSpan.FromSeconds(90), MonitoringWatch.StaleAfter(30));
    }

    [Fact]
    public void Requests_outside_the_offered_choices_are_refused()
    {
        Assert.Null(MonitoringService.Validate(new StartMonitoringRequest(2, 10)));
        Assert.Null(MonitoringService.Validate(new StartMonitoringRequest(0.5, 5, "temperature_2", 110, 130)));
        Assert.NotNull(MonitoringService.Validate(new StartMonitoringRequest(5, 10)));
        Assert.NotNull(MonitoringService.Validate(new StartMonitoringRequest(2, 1)));
        Assert.NotNull(MonitoringService.Validate(new StartMonitoringRequest(2, 10, null, 130, 110)));
    }

    private static DeviceEvent Event(string type, object payload) => new()
    {
        Id = "01J00000000000000000000000", DeviceId = "heat-kitchen", Type = type, Severity = EventSeverity.Alarm, Ts = Now,
        Payload = JsonSerializer.SerializeToDocument(payload),
    };

    [Fact]
    public void Telegram_says_the_reading_and_the_limit_or_how_long_it_is_silent()
    {
        var low = Texts.Event(Event("monitor_threshold", new { metric = "temperature", value = 104.56, limit = 110.0 }), "Кухня", Now);
        Assert.Contains("Вышло из режима", low);
        Assert.Contains("сейчас 104,6° (нижняя граница 110°)", low);
        var high = Texts.Event(Event("monitor_threshold", new { metric = "temperature", value = 131.0, limit = 130.0 }), "Кухня", Now);
        Assert.Contains("(верхняя граница 130°)", high);
        var silent = Texts.Event(Event("monitor_stale", new { metric = "temperature", silentS = 35, intervalS = 10 }), "Кухня", Now);
        Assert.Contains("Термометр замолчал", silent);
        Assert.Contains("нет данных 35 с (ждали каждые 10 с)", silent);
    }
}
