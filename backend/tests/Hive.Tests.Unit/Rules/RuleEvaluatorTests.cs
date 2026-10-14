using Hive.Modules.Rules;
using Hive.Modules.Weather;

namespace Hive.Tests.Unit.Rules;

public sealed class RuleEvaluatorTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T12:00:00+03:00");
    private static readonly RuleAction Heat = new("command", Device: "heat-living1", Command: "relay");

    private static RuleDefinition Rule(params RuleCondition[] conditions) =>
        new(new RuleTrigger("telemetry", Device: "meteo-living1", Metric: "temperature"), conditions, [Heat]);

    private static RuleCondition Below(double value, double? release = null, int forS = 0) =>
        new("value", Device: "meteo-living1", Metric: "temperature", Op: "<", Value: value, Release: release, ForS: forS);

    /// <summary>Readings every minute up to now, the last one being the last value given.</summary>
    private static RuleData Readings(params double[] values)
    {
        var data = new RuleData();
        for (var i = 0; i < values.Length; i++)
            data.Add("meteo-living1", "temperature", Now.AddMinutes(i - values.Length + 1), values[i]);
        data.Sort();
        return data;
    }

    private static EvalOutcome Judge(RuleDefinition rule, RuleData data, Dictionary<int, bool>? latches = null, DateTimeOffset? at = null) =>
        RuleEvaluator.Evaluate(rule, data, at ?? Now, Moscow, latches ?? []).Outcome;

    [Fact]
    public void A_value_below_the_threshold_passes_and_above_fails()
    {
        Assert.Equal(EvalOutcome.Pass, Judge(Rule(Below(12)), Readings(11.5)));
        Assert.Equal(EvalOutcome.Fail, Judge(Rule(Below(12)), Readings(12.5)));
    }

    [Fact]
    public void The_trigger_value_is_reported_for_the_message()
    {
        var verdict = RuleEvaluator.Evaluate(Rule(Below(12)), Readings(11.5), Now, Moscow, new Dictionary<int, bool>());
        Assert.Equal(11.5, verdict.TriggerValue);
    }

    [Fact]
    public void A_value_must_hold_for_the_whole_window()
    {
        // Ten minutes below 12 except one spike: not held.
        Assert.Equal(EvalOutcome.Fail, Judge(Rule(Below(12, forS: 600)), Readings(11, 11, 11, 12.5, 11, 11, 11, 11, 11, 11, 11, 11)));
        Assert.Equal(EvalOutcome.Pass, Judge(Rule(Below(12, forS: 600)), Readings(11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11)));
        // Not enough history to know it held that long.
        Assert.Equal(EvalOutcome.Fail, Judge(Rule(Below(12, forS: 600)), Readings(11, 11)));
    }

    [Fact]
    public void Hysteresis_keeps_the_condition_true_until_the_release_value()
    {
        var rule = Rule(Below(12, release: 14));
        var latches = new Dictionary<int, bool>();
        Assert.Equal(EvalOutcome.Fail, Judge(rule, Readings(13), latches));
        Assert.Equal(EvalOutcome.Pass, Judge(rule, Readings(11.8), latches));
        Assert.Equal(EvalOutcome.Pass, Judge(rule, Readings(13.5), latches)); // still warming up, keeps going
        Assert.Equal(EvalOutcome.Fail, Judge(rule, Readings(14.2), latches));
        Assert.Equal(EvalOutcome.Fail, Judge(rule, Readings(13.5), latches)); // released: needs to fall below 12 again
    }

    [Fact]
    public void Old_readings_mean_no_data_not_a_decision()
    {
        var data = new RuleData();
        data.Add("meteo-living1", "temperature", Now.AddMinutes(-20), 5);
        Assert.Equal(EvalOutcome.NoData, Judge(Rule(Below(12)), data));
        Assert.Equal(EvalOutcome.NoData, Judge(Rule(Below(12)), new RuleData()));
    }

    [Fact]
    public void Not_turns_a_condition_around()
    {
        var notArmed = new RuleCondition("mode", Not: true, Mode: "armed", Is: "true");
        var data = new RuleData { Modes = new(StringComparer.OrdinalIgnoreCase) { ["armed"] = "false" } };
        Assert.Equal(EvalOutcome.Pass, Judge(Rule(notArmed), data));
        data.Modes["armed"] = "true";
        Assert.Equal(EvalOutcome.Fail, Judge(Rule(notArmed), data));
    }

    [Theory]
    [InlineData("2026-10-14T23:30:00+03:00", true)]
    [InlineData("2026-10-15T05:59:00+03:00", true)]
    [InlineData("2026-10-15T06:00:00+03:00", false)]
    [InlineData("2026-10-14T12:00:00+03:00", false)]
    public void A_time_window_may_pass_midnight(string at, bool inside)
    {
        var night = new RuleCondition("time", From: "22:00", To: "06:00");
        Assert.Equal(inside ? EvalOutcome.Pass : EvalOutcome.Fail, Judge(Rule(night), new RuleData(), at: DateTimeOffset.Parse(at)));
    }

    [Fact]
    public void Days_of_the_week_limit_a_time_window()
    {
        var weekdays = new RuleCondition("time", From: "06:00", To: "08:00", Days: [1, 2, 3, 4, 5]);
        Assert.Equal(EvalOutcome.Pass, Judge(Rule(weekdays), new RuleData(), at: DateTimeOffset.Parse("2026-10-14T07:00:00+03:00"))); // Wednesday
        Assert.Equal(EvalOutcome.Fail, Judge(Rule(weekdays), new RuleData(), at: DateTimeOffset.Parse("2026-10-17T07:00:00+03:00"))); // Saturday
    }

    /// <summary>Two days back and two ahead, hourly; sunrise 08:00 and sunset 18:00 every day.</summary>
    private static RuleData Weather(double rainLastNight, int chanceAhead)
    {
        var hours = new List<WeatherHourDto>();
        for (var h = -48; h <= 48; h++)
        {
            var mm = h is -6 or -5 ? rainLastNight / 2 : 0;
            hours.Add(new WeatherHourDto(Now.AddHours(h), 5, null, h > 0 ? chanceAhead : 0, mm, 3, null, null, null));
        }
        var days = new[] { -1, 0, 1 }.Select(d =>
        {
            var date = Now.Date.AddDays(d);
            return new WeatherDayDto(DateOnly.FromDateTime(date), 0, 10, 0, null, 3,
                new DateTimeOffset(date.AddHours(8), TimeSpan.FromHours(3)), new DateTimeOffset(date.AddHours(18), TimeSpan.FromHours(3)));
        }).ToList();
        return new RuleData { Hours = hours, Days = days };
    }

    [Fact]
    public void Watering_is_skipped_after_rain_or_when_rain_is_coming()
    {
        var water = Rule(new RuleCondition("rain_past", Not: true, Mm: 3, Hours: 24), new RuleCondition("rain_forecast", Not: true, Hours: 12, Probability: 60));
        Assert.Equal(EvalOutcome.Pass, Judge(water, Weather(rainLastNight: 0.5, chanceAhead: 20)));
        Assert.Equal(EvalOutcome.Fail, Judge(water, Weather(rainLastNight: 6, chanceAhead: 20)));
        Assert.Equal(EvalOutcome.Fail, Judge(water, Weather(rainLastNight: 0, chanceAhead: 80)));
        Assert.Equal(EvalOutcome.NoData, Judge(water, new RuleData()));
    }

    [Fact]
    public void Dark_follows_sunrise_and_sunset()
    {
        var dark = Rule(new RuleCondition("dark"));
        Assert.Equal(EvalOutcome.Fail, Judge(dark, Weather(0, 0)));
        Assert.Equal(EvalOutcome.Pass, Judge(dark, Weather(0, 0), at: Now.AddHours(8)));
    }

    [Fact]
    public void Device_state_compares_a_channel()
    {
        var heaterOn = Rule(new RuleCondition("device_state", Device: "heat-living1", Channel: "heater", Is: "on"));
        var data = new RuleData { States = new() { ["heat-living1"] = new(StringComparer.OrdinalIgnoreCase) { ["heater"] = "on" } } };
        Assert.Equal(EvalOutcome.Pass, Judge(heaterOn, data));
        Assert.Equal(EvalOutcome.Fail, Judge(heaterOn, new RuleData()));
    }

    [Fact]
    public void Validation_names_what_is_missing()
    {
        Assert.Null(RuleEvaluator.Validate(Rule(Below(12, release: 14, forS: 600))));
        Assert.NotNull(RuleEvaluator.Validate(Rule(new RuleCondition("value", Device: "x", Metric: "temperature", Op: "<<", Value: 1))));
        Assert.NotNull(RuleEvaluator.Validate(new RuleDefinition(new RuleTrigger("schedule", At: "25:00"), [], [Heat])));
        Assert.NotNull(RuleEvaluator.Validate(new RuleDefinition(new RuleTrigger("schedule", At: "07:00"), [], [])));
        Assert.NotNull(RuleEvaluator.Validate(new RuleDefinition(new RuleTrigger("sun", Sun: "noon"), [], [Heat])));
        Assert.NotNull(RuleEvaluator.Validate(new RuleDefinition(new RuleTrigger("telemetry"), [], [Heat])));
        Assert.NotNull(RuleEvaluator.Validate(new RuleDefinition(new RuleTrigger("schedule", At: "07:00"), [], [new RuleAction("command", Device: "x", Command: "bad/name")])));
        Assert.NotNull(RuleEvaluator.Validate(new RuleDefinition(new RuleTrigger("schedule", At: "07:00"), [], [new RuleAction("notify", Text: "x", Level: "loud")])));
    }

    [Fact]
    public void A_schedule_is_due_at_its_local_time_on_its_days()
    {
        var weekdays = new RuleTrigger("schedule", At: "07:00", Days: [1, 2, 3, 4, 5]);
        Assert.Equal(DateTimeOffset.Parse("2026-10-14T07:00:00+03:00"), RuleSchedule.LastScheduled(weekdays, Now, Moscow));
        // Sunday noon: the last weekday 07:00 was Friday.
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T07:00:00+03:00"), RuleSchedule.LastScheduled(weekdays, DateTimeOffset.Parse("2026-10-18T12:00:00+03:00"), Moscow));
        Assert.Equal(5, RuleSchedule.Scheduled(weekdays, Now.AddDays(-7), Now, Moscow).Count());
    }

    [Fact]
    public void Sun_times_take_the_offset()
    {
        var beforeSunset = new RuleTrigger("sun", Sun: "sunset", OffsetMin: -30);
        var times = RuleSchedule.SunTimes(beforeSunset, Weather(0, 0).Days).ToList();
        Assert.Equal(new DateTimeOffset(Now.Date.AddHours(17).AddMinutes(30), TimeSpan.FromHours(3)), times[1]);
    }
}
