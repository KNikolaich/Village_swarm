using System.Globalization;
using Hive.Contracts.Mqtt;
using Hive.Modules.Weather;

namespace Hive.Modules.Rules;

/// <summary>
/// What the evaluator may look at, prepared in advance: readings per device and metric (oldest first), the forecast,
/// hive modes and device states. Live runs fill it for "now", dry runs for a whole window.
/// </summary>
public sealed class RuleData
{
    public Dictionary<(string Device, string Metric), List<(DateTimeOffset Ts, double Value)>> Series { get; } = [];
    public IReadOnlyList<WeatherHourDto> Hours { get; init; } = [];
    public IReadOnlyList<WeatherDayDto> Days { get; init; } = [];
    public Dictionary<string, string> Modes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, string>> States { get; init; } = [];

    public void Add(string device, string metric, DateTimeOffset ts, double value)
    {
        if (!Series.TryGetValue((device, metric), out var list))
            Series[(device, metric)] = list = [];
        list.Add((ts, value));
    }

    public void Sort()
    {
        foreach (var list in Series.Values)
            list.Sort((a, b) => a.Ts.CompareTo(b.Ts));
    }

    /// <summary>The last reading at or before <paramref name="at"/>.</summary>
    public (DateTimeOffset Ts, double Value)? LatestAt(string device, string metric, DateTimeOffset at)
    {
        if (!Series.TryGetValue((device, metric), out var list) || list.Count == 0)
            return null;
        int lo = 0, hi = list.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (list[mid].Ts <= at)
            {
                found = mid;
                lo = mid + 1;
            }
            else
                hi = mid - 1;
        }
        return found < 0 ? null : list[found];
    }

    /// <summary>Readings in (from, to].</summary>
    public IEnumerable<double> Between(string device, string metric, DateTimeOffset from, DateTimeOffset to) =>
        Series.TryGetValue((device, metric), out var list) ? list.Where(p => p.Ts > from && p.Ts <= to).Select(p => p.Value) : [];
}

public enum EvalOutcome { Pass, Fail, NoData }

/// <summary>
/// The verdict; <see cref="Reason"/> names the condition that failed or, as a code the UI translates, the data that is
/// missing: stale:{device}:{metric}, no_sun, no_past_weather, no_forecast.
/// </summary>
public sealed record Evaluation(EvalOutcome Outcome, string? Reason, double? TriggerValue);

/// <summary>Checks a rule's conditions against prepared data. Pure: no clock, no database.</summary>
public static class RuleEvaluator
{
    public const int DefaultMaxAgeS = 900;

    /// <summary>
    /// Evaluates the conditions at <paramref name="at"/> in order; the first that fails or lacks data decides.
    /// <paramref name="latches"/> carries the hysteresis state of value conditions (by index) and is updated.
    /// </summary>
    public static Evaluation Evaluate(RuleDefinition rule, RuleData data, DateTimeOffset at, TimeZoneInfo zone, IDictionary<int, bool> latches)
    {
        var trigger = rule.Trigger;
        double? triggerValue = trigger.Kind == "telemetry" && trigger.Device is not null && trigger.Metric is not null
            ? data.LatestAt(trigger.Device, trigger.Metric, at)?.Value
            : null;
        for (var i = 0; i < rule.Conditions.Count; i++)
        {
            var c = rule.Conditions[i];
            var (ok, missing) = Check(c, i, data, at, zone, latches);
            if (missing is not null)
                return new Evaluation(EvalOutcome.NoData, missing, triggerValue);
            if (c.Not)
                ok = !ok;
            if (!ok)
                return new Evaluation(EvalOutcome.Fail, $"{c.Kind} #{i + 1}", triggerValue);
        }
        return new Evaluation(EvalOutcome.Pass, null, triggerValue);
    }

    private static (bool Ok, string? Missing) Check(RuleCondition c, int index, RuleData data, DateTimeOffset at, TimeZoneInfo zone, IDictionary<int, bool> latches)
    {
        switch (c.Kind)
        {
            case "value":
            {
                var latest = data.LatestAt(c.Device!, c.Metric!, at);
                if (latest is null || at - latest.Value.Ts > TimeSpan.FromSeconds(c.MaxAgeS ?? DefaultMaxAgeS))
                    return (false, $"stale:{c.Device}:{c.Metric}");
                var latched = c.Release is not null && latches.TryGetValue(index, out var l) && l;
                var threshold = latched ? c.Release!.Value : c.Value!.Value;
                var ok = Compare(latest.Value.Value, c.Op!, threshold);
                if (ok && c.ForS > 0)
                {
                    // Held for ForS: the reading in force at the start of the window and every one since agree.
                    var start = at.AddSeconds(-c.ForS);
                    var atStart = data.LatestAt(c.Device!, c.Metric!, start);
                    ok = atStart is not null && Compare(atStart.Value.Value, c.Op!, threshold)
                         && data.Between(c.Device!, c.Metric!, start, at).All(v => Compare(v, c.Op!, threshold));
                }
                if (c.Release is not null)
                    latches[index] = ok;
                return (ok, null);
            }
            case "mode":
                return (string.Equals(data.Modes.GetValueOrDefault(c.Mode!), c.Is, StringComparison.OrdinalIgnoreCase), null);
            case "time":
                return (InWindow(TimeZoneInfo.ConvertTime(at, zone), c.From!, c.To!, c.Days), null);
            case "dark":
                return ForecastMath.IsDark(data.Days, at) is { } dark ? (dark, null) : (false, "no_sun");
            case "rain_past":
            {
                var from = at.AddHours(-(c.Hours ?? 24));
                if (!Covers(data.Hours, from, at))
                    return (false, "no_past_weather");
                return (ForecastMath.Rain(data.Hours, from, at).Millimetres >= (c.Mm ?? 1), null);
            }
            case "rain_forecast":
            {
                var to = at.AddHours(c.Hours ?? 12);
                if (!Covers(data.Hours, at, to))
                    return (false, "no_forecast");
                var (mm, probability) = ForecastMath.Rain(data.Hours, at, to);
                return (mm >= (c.Mm ?? 1) || (probability ?? 0) >= (c.Probability ?? 60), null);
            }
            case "device_state":
                return (data.States.TryGetValue(c.Device!, out var state) && state.TryGetValue(c.Channel!, out var v)
                        && string.Equals(v, c.Is, StringComparison.OrdinalIgnoreCase), null);
            default:
                return (false, $"unknown condition {c.Kind}");
        }
    }

    /// <summary>The forecast has hours at both ends of the window (gaps inside are tolerated).</summary>
    private static bool Covers(IReadOnlyList<WeatherHourDto> hours, DateTimeOffset from, DateTimeOffset to) =>
        hours.Count > 0 && hours[0].Time <= from.AddHours(1) && hours[^1].Time >= to.AddHours(-1);

    public static bool Compare(double value, string op, double threshold) => op switch
    {
        "<" => value < threshold,
        "<=" => value <= threshold,
        ">" => value > threshold,
        ">=" => value >= threshold,
        "==" => Math.Abs(value - threshold) < 1e-9,
        "!=" => Math.Abs(value - threshold) >= 1e-9,
        _ => false,
    };

    public static int IsoDay(DateTimeOffset local) => ((int)local.DayOfWeek + 6) % 7 + 1;

    /// <summary>[from, to) in minutes of the day; a window like 22:00-06:00 passes midnight.</summary>
    public static bool InWindow(DateTimeOffset local, string from, string to, int[]? days)
    {
        if (days is { Length: > 0 } && !days.Contains(IsoDay(local)))
            return false;
        var m = local.Hour * 60 + local.Minute;
        var f = Minutes(from);
        var t = Minutes(to);
        return f <= t ? m >= f && m < t : m >= f || m < t;
    }

    public static int Minutes(string hhmm)
    {
        var t = TimeOnly.ParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture);
        return t.Hour * 60 + t.Minute;
    }

    public static bool IsTime(string? hhmm) =>
        hhmm is not null && TimeOnly.TryParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Null when the rule is usable, otherwise what is wrong with it (for the API and the UI).</summary>
    public static string? Validate(RuleDefinition? rule)
    {
        if (rule?.Trigger is null)
            return "A trigger is required";
        var t = rule.Trigger;
        var triggerError = t.Kind switch
        {
            "telemetry" when string.IsNullOrEmpty(t.Device) || string.IsNullOrEmpty(t.Metric) => "telemetry trigger needs device and metric",
            "event" when string.IsNullOrEmpty(t.EventType) => "event trigger needs eventType",
            "schedule" when !IsTime(t.At) => "schedule trigger needs at = HH:mm",
            "sun" when t.Sun is not ("sunrise" or "sunset") || Math.Abs(t.OffsetMin) > 720 => "sun trigger needs sun = sunrise|sunset and an offset within 12 h",
            "offline" when string.IsNullOrEmpty(t.Device) || t.OfflineMin is not (>= 1 and <= 10080) => "offline trigger needs device and offlineMin 1..10080",
            "telemetry" or "event" or "schedule" or "sun" or "offline" => null,
            _ => $"unknown trigger {t.Kind}",
        };
        if (triggerError is not null)
            return triggerError;
        if (t.Days is { } td && td.Any(d => d is < 1 or > 7))
            return "days are 1 (Monday) .. 7 (Sunday)";

        foreach (var c in rule.Conditions ?? [])
        {
            var error = c.Kind switch
            {
                "value" when string.IsNullOrEmpty(c.Device) || string.IsNullOrEmpty(c.Metric) || c.Value is null
                             || c.Op is not ("<" or "<=" or ">" or ">=" or "==" or "!=") => "value condition needs device, metric, op and value",
                "value" when c.ForS is < 0 or > 86400 => "forS is 0..86400",
                "mode" when string.IsNullOrEmpty(c.Mode) || c.Is is null => "mode condition needs mode and is",
                "time" when !IsTime(c.From) || !IsTime(c.To) => "time condition needs from and to = HH:mm",
                "device_state" when string.IsNullOrEmpty(c.Device) || string.IsNullOrEmpty(c.Channel) || c.Is is null => "device_state condition needs device, channel and is",
                "rain_past" or "rain_forecast" when c.Hours is not null and not (>= 1 and <= 72) => "hours is 1..72",
                "value" or "mode" or "time" or "dark" or "rain_past" or "rain_forecast" or "device_state" => null,
                _ => $"unknown condition {c.Kind}",
            };
            if (error is not null)
                return error;
            if (c.Days is { } cd && cd.Any(d => d is < 1 or > 7))
                return "days are 1 (Monday) .. 7 (Sunday)";
        }

        if (rule.Actions is not { Count: > 0 })
            return "At least one action is required";
        foreach (var a in rule.Actions)
        {
            var error = a.Kind switch
            {
                "command" when string.IsNullOrEmpty(a.Device) || !Topics.IsValidSegment(a.Command ?? "") => "command action needs device and a command name",
                "notify" when string.IsNullOrWhiteSpace(a.Text) || a.Level is not (null or "info" or "warn" or "alarm" or "critical") => "notify action needs text and a level info|warn|alarm|critical",
                "set_mode" when string.IsNullOrEmpty(a.Mode) || a.Value is null => "set_mode action needs mode and value",
                "snapshot" when string.IsNullOrEmpty(a.Device) => "snapshot action needs a camera",
                "command" or "notify" or "set_mode" or "snapshot" => null,
                _ => $"unknown action {a.Kind}",
            };
            if (error is not null)
                return error;
        }
        return rule.CooldownS is < 0 or > 604800 ? "cooldownS is 0..604800" : null;
    }
}
