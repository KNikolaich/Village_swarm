using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Contracts;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Modules.Devices;
using Hive.Modules.Telemetry.Ingest;
using Hive.Modules.Weather;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Rules;

/// <summary>When time-based triggers are due. Pure, for the scheduler and the dry run alike.</summary>
public static class RuleSchedule
{
    /// <summary>The latest moment at or before <paramref name="now"/> a schedule trigger was due (house time zone).</summary>
    public static DateTimeOffset? LastScheduled(RuleTrigger t, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var minutes = RuleEvaluator.Minutes(t.At!);
        for (var back = 0; back <= 7; back++)
        {
            var day = local.Date.AddDays(-back);
            var at = day.AddMinutes(minutes);
            var occurrence = new DateTimeOffset(at, zone.GetUtcOffset(at));
            if (occurrence > now)
                continue;
            if (t.Days is { Length: > 0 } && !t.Days.Contains(RuleEvaluator.IsoDay(occurrence)))
                continue;
            return occurrence;
        }
        return null;
    }

    /// <summary>Every moment a schedule trigger was due in (from, to].</summary>
    public static IEnumerable<DateTimeOffset> Scheduled(RuleTrigger t, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(from, zone).Date;
        var minutes = RuleEvaluator.Minutes(t.At!);
        for (var day = local; day <= TimeZoneInfo.ConvertTime(to, zone).Date; day = day.AddDays(1))
        {
            var at = day.AddMinutes(minutes);
            var occurrence = new DateTimeOffset(at, zone.GetUtcOffset(at));
            if (occurrence > from && occurrence <= to && (t.Days is not { Length: > 0 } || t.Days.Contains(RuleEvaluator.IsoDay(occurrence))))
                yield return occurrence;
        }
    }

    /// <summary>Sunrise or sunset (plus offset) of every day of the forecast.</summary>
    public static IEnumerable<DateTimeOffset> SunTimes(RuleTrigger t, IEnumerable<WeatherDayDto> days) =>
        days.Select(d => t.Sun == "sunrise" ? d.Sunrise : d.Sunset).OfType<DateTimeOffset>().Select(s => s.AddMinutes(t.OffsetMin)).Order();
}

/// <summary>Enabled rules, cached; the API drops the cache when a rule changes.</summary>
public sealed class RuleStore(IServiceScopeFactory scopes)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private volatile IReadOnlyList<(int Id, RuleDefinition Definition)>? _rules;

    public void Invalidate() => _rules = null;

    public async Task<IReadOnlyList<(int Id, RuleDefinition Definition)>> GetAsync(CancellationToken ct)
    {
        if (_rules is { } cached)
            return cached;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var rows = await db.Rules.AsNoTracking().Where(r => r.Enabled).Select(r => new { r.Id, r.Definition }).ToListAsync(ct);
        var rules = rows.Select(r => (r.Id, r.Definition.Deserialize<RuleDefinition>(Json)!)).ToList();
        _rules = rules;
        return rules;
    }

    public static RuleDefinition Read(JsonDocument doc) => doc.Deserialize<RuleDefinition>(Json)!;

    public static JsonDocument Write(RuleDefinition rule) => JsonSerializer.SerializeToDocument(rule, Json);
}

/// <summary>Builds the data a rule needs to be judged "now" or over a past window.</summary>
public sealed class RuleDataLoader(HiveDbContext db, WeatherService weather)
{
    public async Task<RuleData> LoadAsync(RuleDefinition rule, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var series = rule.Conditions.Where(c => c.Kind == "value").Select(c => (c.Device!, c.Metric!, Back: Math.Max(c.ForS, c.MaxAgeS ?? RuleEvaluator.DefaultMaxAgeS))).ToList();
        if (rule.Trigger is { Kind: "telemetry", Device: { } td, Metric: { } tm })
            series.Add((td, tm, RuleEvaluator.DefaultMaxAgeS));

        var needsWeather = rule.Trigger.Kind == "sun" || rule.Conditions.Any(c => c.Kind is "dark" or "rain_past" or "rain_forecast");
        WeatherDto? forecast = needsWeather ? await weather.GetAsync(ct) : null;
        var data = new RuleData
        {
            Hours = forecast?.Hourly ?? [],
            Days = forecast?.Daily ?? [],
            Modes = await db.Modes.AsNoTracking().ToDictionaryAsync(m => m.Key, m => m.Value, StringComparer.OrdinalIgnoreCase, ct),
        };

        foreach (var group in series.GroupBy(s => (s.Item1, s.Item2)))
        {
            var start = from.AddSeconds(-group.Max(g => g.Back) - 60);
            var (device, metric) = group.Key;
            var points = await db.Telemetry.AsNoTracking()
                .Where(p => p.DeviceId == device && p.Metric == metric && p.Ts >= start && p.Ts <= to)
                .Select(p => new { p.Ts, p.Value }).ToListAsync(ct);
            foreach (var p in points)
                data.Add(device, metric, p.Ts, p.Value);
        }
        data.Sort();

        var stateDevices = rule.Conditions.Where(c => c.Kind == "device_state").Select(c => c.Device!).Distinct().ToList();
        if (stateDevices.Count > 0)
        {
            var states = await db.Devices.AsNoTracking().Where(d => stateDevices.Contains(d.DeviceId) && d.State != null)
                .Select(d => new { d.DeviceId, d.State }).ToListAsync(ct);
            foreach (var s in states)
                data.States[s.DeviceId] = s.State!.RootElement.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
        }
        return data;
    }
}

/// <summary>Judges one rule now and, when it passes, carries out its actions and writes the journal.</summary>
public sealed class RuleRunner(
    HiveDbContext db, RuleDataLoader loader, CommandService commands, ArmService arm, IngestQueue ingest,
    IOptions<HiveOptions> hive, TimeProvider time, ILogger<RuleRunner> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task RunAsync(int ruleId, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await RunLockedAsync(ruleId, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task RunLockedAsync(int ruleId, CancellationToken ct)
    {
        var row = await db.Rules.FirstOrDefaultAsync(r => r.Id == ruleId && r.Enabled, ct);
        if (row is null)
            return;
        var rule = RuleStore.Read(row.Definition);
        var now = time.GetUtcNow();
        if (row.LastFiredAt is { } last && now - last < TimeSpan.FromSeconds(rule.CooldownS))
            return;

        var data = await loader.LoadAsync(rule, now, now, ct);
        var latches = ReadLatches(row.State);
        var before = new Dictionary<int, bool>(latches);
        var verdict = RuleEvaluator.Evaluate(rule, data, now, hive.Value.Zone, latches);
        // Written only when a latch moved: readings come every few seconds and the hive lives on a small drive.
        if (latches.Count != before.Count || latches.Any(l => !before.TryGetValue(l.Key, out var was) || was != l.Value))
            row.State = JsonSerializer.SerializeToDocument(latches.ToDictionary(k => k.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), k => k.Value));

        switch (verdict.Outcome)
        {
            case EvalOutcome.NoData:
                // Said once per stretch without data, not on every reading.
                if (row.LastOutcome != "no_data")
                {
                    Log(row, now, "no_data", new { reason = verdict.Reason });
                    Notify(now, "warn", row.Name, $"Правило «{row.Name}» не может работать: {Explain(verdict.Reason)}");
                    row.LastOutcome = "no_data";
                }
                break;
            case EvalOutcome.Pass:
                var results = new List<object>();
                var failed = false;
                foreach (var action in rule.Actions)
                {
                    var (ok, result) = await ActAsync(row, action, verdict.TriggerValue, now, ct);
                    failed |= !ok;
                    results.Add(new { action = action.Kind, device = action.Device, result });
                }
                row.LastFiredAt = now;
                row.LastOutcome = failed ? "error" : "fired";
                Log(row, now, row.LastOutcome, new { value = verdict.TriggerValue, actions = results });
                break;
            default:
                if (row.LastOutcome == "no_data")
                    row.LastOutcome = null; // data is back
                break;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<(bool Ok, string Result)> ActAsync(Rule row, RuleAction action, double? value, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            switch (action.Kind)
            {
                case "command":
                {
                    var (status, _) = await commands.SendAsync(action.Device!, action.Command!, action.Payload?.DeepClone().AsObject(), $"rule:{row.Id}", null, ct);
                    return (status == SendStatus.Sent, status.ToString().ToLowerInvariant());
                }
                case "snapshot":
                {
                    var (status, _) = await commands.SendAsync(action.Device!, "snapshot", new JsonObject { ["count"] = 1 }, $"rule:{row.Id}", 30, ct);
                    return (status == SendStatus.Sent, status.ToString().ToLowerInvariant());
                }
                case "notify":
                {
                    // {device} is the hornet the rule is about: the one named in the action, else the trigger's.
                    var about = action.Device ?? RuleStore.Read(row.Definition).Trigger.Device;
                    var deviceName = about is null ? null : await db.Devices.AsNoTracking().Where(d => d.DeviceId == about).Select(d => d.Name).FirstOrDefaultAsync(ct) ?? about;
                    Notify(now, action.Level ?? "warn", row.Name, Fill(action.Text!, row.Name, value, deviceName));
                    return (true, "queued");
                }
                case "set_mode":
                    if (string.Equals(action.Mode, Mode.Armed, StringComparison.OrdinalIgnoreCase))
                    {
                        // Arming goes through the guard service so the cameras are told too.
                        await arm.SetAsync(action.Value is "true" or "on" or "1", $"rule:{row.Id}", ct);
                        return (true, "set");
                    }
                    var mode = await db.Modes.FirstOrDefaultAsync(m => m.Key == action.Mode, ct);
                    if (mode is null)
                        db.Modes.Add(new Mode { Key = action.Mode!, Value = action.Value!, UpdatedAt = now, UpdatedBy = $"rule:{row.Id}" });
                    else
                    {
                        mode.Value = action.Value!;
                        mode.UpdatedAt = now;
                        mode.UpdatedBy = $"rule:{row.Id}";
                    }
                    return (true, "set");
                default:
                    return (false, "unknown action");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Rule {Rule} action {Action} failed", row.Id, action.Kind);
            return (false, ex.Message);
        }
    }

    /// <summary>The missing-data code in words for Telegram (the hive's messages there are Russian).</summary>
    internal static string Explain(string? reason) => reason?.Split(':') switch
    {
        ["stale", var device, var metric] => $"нет свежих данных «{metric}» от {device}",
        ["no_sun"] => "в прогнозе нет восхода и заката",
        ["no_past_weather"] => "нет данных о погоде за прошедшие часы",
        ["no_forecast"] => "нет прогноза на ближайшие часы",
        _ => reason ?? "нет данных",
    };

    private static string Fill(string text, string rule, double? value, string? device) => text
        .Replace("{rule}", rule, StringComparison.Ordinal)
        .Replace("{device}", device ?? "", StringComparison.Ordinal)
        .Replace("{value}", value?.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) ?? "—", StringComparison.Ordinal);

    /// <summary>Into the event feed as an event of the hive itself; warn and above also reach Telegram (spec 6.7).</summary>
    private void Notify(DateTimeOffset now, string level, string rule, string text)
    {
        var severity = level switch { "info" => EventSeverity.Info, "alarm" => EventSeverity.Alarm, "critical" => EventSeverity.Critical, _ => EventSeverity.Warn };
        var payload = JsonSerializer.SerializeToDocument(new { rule, text });
        ingest.Enqueue(new DeviceEventReceived($"hive-{hive.Value.Node}", Ulid.New(now), now, now, RuleEngine.EventType, severity, null, payload));
    }

    private void Log(Rule row, DateTimeOffset now, string outcome, object details) =>
        db.RuleRuns.Add(new RuleRun { RuleId = row.Id, Ts = now, Outcome = outcome, Details = JsonSerializer.SerializeToDocument(details) });

    private static Dictionary<int, bool> ReadLatches(JsonDocument? state)
    {
        var latches = new Dictionary<int, bool>();
        if (state?.RootElement.ValueKind == JsonValueKind.Object)
            foreach (var p in state.RootElement.EnumerateObject())
                if (int.TryParse(p.Name, out var i) && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    latches[i] = p.Value.GetBoolean();
        return latches;
    }
}

/// <summary>Reading- and event-triggered rules: looked at as soon as the data is stored.</summary>
public sealed class RuleEngine(RuleStore store, IServiceScopeFactory scopes, ILogger<RuleEngine> logger) : IIngestListener
{
    /// <summary>Event type of the rules' own notifications; never a trigger, so a rule cannot set itself off.</summary>
    public const string EventType = "rule";

    public async Task OnIngestedAsync(IReadOnlyList<IngestItem> items, CancellationToken ct)
    {
        var rules = await store.GetAsync(ct);
        if (rules.Count == 0)
            return;
        var due = new HashSet<int>();
        foreach (var (id, rule) in rules)
        {
            var t = rule.Trigger;
            foreach (var item in items)
            {
                var matches = t.Kind switch
                {
                    "telemetry" => item is TelemetryReceived r && r.DeviceId == t.Device && r.Values.Any(v => v.Metric == t.Metric),
                    "event" => item is DeviceEventReceived e && e.Type != EventType && e.Type == t.EventType && (t.Device is null || e.DeviceId == t.Device),
                    _ => false,
                };
                if (matches)
                {
                    due.Add(id);
                    break;
                }
            }
        }
        foreach (var id in due)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<RuleRunner>().RunAsync(id, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Rule {Rule} failed", id);
            }
        }
    }
}

/// <summary>Time-based rules (schedule, sunrise/sunset) and offline rules, checked every 30 seconds.</summary>
public sealed class RuleScheduler(RuleStore store, IServiceScopeFactory scopes, IOptions<HiveOptions> hive, TimeProvider time, ILogger<RuleScheduler> logger) : BackgroundService
{
    /// <summary>A due moment missed by more than this (the hive was down) is skipped, not caught up.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    // The due moment each rule was last looked at: a rule whose conditions did not hold is not asked again until the next one.
    private readonly Dictionary<int, DateTimeOffset> _checked = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The build runs the app to write the OpenAPI document; it must not need a database.
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Rule scheduler round failed");
            }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var rules = (await store.GetAsync(ct)).Where(r => r.Definition.Trigger.Kind is "schedule" or "sun" or "offline").ToList();
        if (rules.Count == 0)
            return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var now = time.GetUtcNow();
        var lastFired = await db.Rules.AsNoTracking().Where(r => r.Enabled).ToDictionaryAsync(r => r.Id, r => r.LastFiredAt, ct);
        WeatherDto? forecast = null;

        foreach (var (id, rule) in rules)
        {
            var t = rule.Trigger;
            var last = lastFired.GetValueOrDefault(id);
            DateTimeOffset? due = null;
            switch (t.Kind)
            {
                case "schedule":
                    due = RuleSchedule.LastScheduled(t, now, hive.Value.Zone);
                    break;
                case "sun":
                    forecast ??= await scope.ServiceProvider.GetRequiredService<WeatherService>().GetAsync(ct);
                    var past = RuleSchedule.SunTimes(t, forecast.Daily).Where(s => s <= now).ToList();
                    due = past.Count > 0 ? past[^1] : null;
                    break;
                case "offline":
                    var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == t.Device, ct);
                    // Once per outage: due when it has been quiet long enough; the outage is told apart by when it began.
                    if (device is { Status: DeviceStatus.Offline, LastSeenAt: { } seen } && now - seen >= TimeSpan.FromMinutes(t.OfflineMin ?? 10))
                        due = seen.AddMinutes(t.OfflineMin ?? 10);
                    if (due is { } o && (last is null || last < o) && _checked.GetValueOrDefault(id) != o)
                    {
                        _checked[id] = o;
                        await RunAsync(scope, id, ct);
                    }
                    continue;
            }
            if (due is { } d && now - d <= Grace && (last is null || last < d) && _checked.GetValueOrDefault(id) != d)
            {
                _checked[id] = d;
                await RunAsync(scope, id, ct);
            }
        }
    }

    private async Task RunAsync(IServiceScope scope, int id, CancellationToken ct)
    {
        try
        {
            await scope.ServiceProvider.GetRequiredService<RuleRunner>().RunAsync(id, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Rule {Rule} failed", id);
        }
    }
}
