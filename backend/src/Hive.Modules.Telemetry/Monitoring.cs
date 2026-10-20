using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Contracts;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hive.Modules.Telemetry;

/// <summary>
/// PUT /api/telemetry/monitoring/{deviceId}: watch a thermometer closely for a while. Below and Above are optional
/// limits of <see cref="Metric"/>; a reading outside them raises an alarm.
/// </summary>
public sealed record StartMonitoringRequest(double Hours, int IntervalS, string? Metric = null, double? Below = null, double? Above = null);

/// <summary>A monitoring session in progress. Breach is "below" or "above" while a limit is crossed; StaleSince is set while readings stopped.</summary>
public sealed record MonitoringDto(
    string DeviceId, DateTimeOffset StartedAt, DateTimeOffset Until, int IntervalS, string Metric, double? Below, double? Above,
    string? Breach, DateTimeOffset? StaleSince);

public enum MonitoringStatus { Ok, NotFound, BadRequest }

/// <summary>What device.monitor holds: the session plus the reporting interval to give back when it ends (null: none set).</summary>
public sealed record MonitorState(
    DateTimeOffset StartedAt, DateTimeOffset Until, int IntervalS, string Metric, double? Below, double? Above, int? RestoreTeleS,
    string? StartedBy, string? Breach = null, DateTimeOffset? StaleSince = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static MonitorState? Read(Device d) => d.Monitor?.Deserialize<MonitorState>(Json);

    public static void Write(Device d, MonitorState? state) =>
        d.Monitor = state is null ? null : JsonSerializer.SerializeToDocument(state, Json);

    public MonitoringDto ToDto(string deviceId) => new(deviceId, StartedAt, Until, IntervalS, Metric, Below, Above, Breach, StaleSince);
}

/// <summary>
/// Monitoring mode: a thermometer reports every few seconds for a chosen time (a pressure cooker, a smokehouse), then goes
/// back to its usual interval by itself. While it lasts the hive raises an alarm when a reading leaves the given limits
/// or when readings stop coming. The interval goes to the hornet as tele_interval_s in its retained config.
/// </summary>
public sealed class MonitoringService(HiveDbContext db, MqttGateway mqtt, TimeProvider time)
{
    public static readonly double[] Hours = [0.5, 1, 2, 3, 4, 6, 8, 12];
    public static readonly int[] Intervals = [5, 10, 15, 30];

    /// <summary>Our firmware roles that read and send at tele_interval_s. Tasmota and Zigbee devices keep their own timing.</summary>
    public static readonly string[] Types = ["meteo", "heat", "ble"];

    public static string? Validate(StartMonitoringRequest r)
    {
        if (!Hours.Contains(r.Hours))
            return "hours: " + string.Join(", ", Hours.Select(h => h.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (!Intervals.Contains(r.IntervalS))
            return "intervalS: " + string.Join(", ", Intervals);
        if (r.Metric is { } m && (m.Length is 0 or > 64))
            return "metric: от 1 до 64 символов";
        if (r.Below is { } lo && r.Above is { } hi && lo >= hi)
            return "Нижняя граница должна быть меньше верхней";
        return null;
    }

    /// <summary>Why this device cannot be monitored, or null when it can.</summary>
    public static string? Unsupported(Device d) =>
        d.Driver != Drivers.Hornet || !Types.Contains(d.TypeCode) ? "Мониторинг умеют только наши термометры (meteo, heat, ble)"
        : d.Status == DeviceStatus.Disabled ? "Шершень удалён"
        : d.Config?.RootElement.TryGetProperty("sleep_s", out var s) == true && s.TryGetInt32(out var v) && v > 0 ? "Шершень спит между замерами: мониторинг не для батарейных"
        : null;

    public async Task<IReadOnlyList<MonitoringDto>> ListAsync(CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().Where(d => d.Monitor != null).OrderBy(d => d.DeviceId).ToListAsync(ct);
        return devices.Select(d => MonitorState.Read(d)?.ToDto(d.DeviceId)).OfType<MonitoringDto>().ToList();
    }

    /// <summary>Starts a session, or changes the one running (its end is counted again from now).</summary>
    public async Task<(MonitoringStatus Status, string? Error, MonitoringDto? Session)> StartAsync(
        string deviceId, StartMonitoringRequest r, string by, CancellationToken ct)
    {
        if (Validate(r) is { } invalid)
            return (MonitoringStatus.BadRequest, invalid, null);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return (MonitoringStatus.NotFound, null, null);
        if (Unsupported(device) is { } why)
            return (MonitoringStatus.BadRequest, why, null);

        var now = time.GetUtcNow();
        var running = MonitorState.Read(device);
        var restore = running is null ? TeleIntervalS(device) : running.RestoreTeleS;
        var metric = string.IsNullOrEmpty(r.Metric) ? "temperature" : r.Metric;
        var state = new MonitorState(running?.StartedAt ?? now, now.AddHours(r.Hours), r.IntervalS, metric, r.Below, r.Above, restore, by);
        MonitorState.Write(device, state);
        var config = SetTeleInterval(device, r.IntervalS);
        Audit.Record(db, now, by, "monitoring_start", deviceId, new { r.Hours, r.IntervalS, metric, r.Below, r.Above });
        await db.SaveChangesAsync(ct);
        await PublishAsync(deviceId, config, ct);
        return (MonitoringStatus.Ok, null, state.ToDto(deviceId));
    }

    /// <summary>Ends a session now: the hornet gets its usual interval back. False when nothing was running.</summary>
    public async Task<bool> StopAsync(string deviceId, string by, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null || MonitorState.Read(device) is not { } state)
            return false;
        var config = End(device, state);
        Audit.Record(db, time.GetUtcNow(), by, "monitoring_stop", deviceId);
        await db.SaveChangesAsync(ct);
        await PublishAsync(deviceId, config, ct);
        return true;
    }

    /// <summary>Clears the session and puts the interval back; the caller saves and publishes the returned config.</summary>
    internal static string End(Device device, MonitorState state)
    {
        MonitorState.Write(device, null);
        return SetTeleInterval(device, state.RestoreTeleS);
    }

    internal static int? TeleIntervalS(Device d) =>
        d.Config?.RootElement.TryGetProperty("tele_interval_s", out var t) == true && t.TryGetInt32(out var v) ? v : null;

    /// <summary>Writes tele_interval_s (null removes it: the firmware default) into the config with a new rev.</summary>
    private static string SetTeleInterval(Device device, int? seconds)
    {
        var config = device.Config is { } current
            ? JsonNode.Parse(current.RootElement.GetRawText())!.AsObject()
            : new JsonObject { ["v"] = 1, ["name"] = device.Name, ["health_interval_s"] = 60 };
        if (seconds is { } s)
            config["tele_interval_s"] = s;
        else
            config.Remove("tele_interval_s");
        device.ConfigRev++;
        config["rev"] = device.ConfigRev;
        var text = config.ToJsonString();
        device.Config = JsonDocument.Parse(text);
        return text;
    }

    private async Task PublishAsync(string deviceId, string config, CancellationToken ct)
    {
        if (mqtt.IsConnected)
            await mqtt.PublishAsync(Topics.Config(deviceId), config, retain: true, ct);
    }
}

/// <summary>
/// Readings of monitored devices checked as they are written: crossing a limit raises an alarm (monitor_threshold), coming
/// back inside it by <see cref="Hysteresis"/> tells the all clear (monitor_normal), so a reading wobbling on the line
/// does not ring again and again.
/// </summary>
public sealed class MonitoringLimits(IServiceScopeFactory scopes, IngestQueue ingest, TimeProvider time) : IIngestListener
{
    public const double Hysteresis = 0.5;

    public async Task OnIngestedAsync(IReadOnlyList<IngestItem> items, CancellationToken ct)
    {
        var readings = items.OfType<TelemetryReceived>().ToList();
        if (readings.Count == 0)
            return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var ids = readings.Select(r => r.DeviceId).Distinct().ToList();
        var devices = await db.Devices.Where(d => d.Monitor != null && ids.Contains(d.DeviceId)).ToListAsync(ct);
        if (devices.Count == 0)
            return;

        var now = time.GetUtcNow();
        foreach (var device in devices)
        {
            if (MonitorState.Read(device) is not { } state || (state.Below is null && state.Above is null))
                continue;
            var values = readings.Where(r => r.DeviceId == device.DeviceId).OrderBy(r => r.Ts)
                .SelectMany(r => r.Values.Where(v => v.Metric == state.Metric).Select(v => (r.Ts, v.Value)));
            foreach (var (ts, value) in values)
            {
                var (breach, events) = Step(state, value);
                if (breach == state.Breach)
                    continue;
                state = state with { Breach = breach };
                foreach (var (type, severity, limit) in events)
                    ingest.Enqueue(new DeviceEventReceived(device.DeviceId, Ulid.New(now), ts, now, type, severity, null,
                        JsonSerializer.SerializeToDocument(new { metric = state.Metric, value, limit })));
            }
            MonitorState.Write(device, state);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The breach after this reading and the events it causes. Pure.</summary>
    public static (string? Breach, IReadOnlyList<(string Type, EventSeverity Severity, double Limit)> Events) Step(MonitorState s, double value)
    {
        string? breach = value < s.Below ? "below" : value > s.Above ? "above" : null;
        if (breach is null && s.Breach is not null)
        {
            // Back only once clear of the line it crossed.
            var clear = s.Breach == "below" ? value >= s.Below + Hysteresis : value <= s.Above - Hysteresis;
            return clear ? (null, [("monitor_normal", EventSeverity.Info, Limit(s, s.Breach))]) : (s.Breach, NoEvents);
        }
        return breach is null || breach == s.Breach
            ? (breach, NoEvents)
            : (breach, [("monitor_threshold", EventSeverity.Alarm, Limit(s, breach))]);
    }

    private static readonly IReadOnlyList<(string Type, EventSeverity Severity, double Limit)> NoEvents = [];

    private static double Limit(MonitorState s, string side) => side == "below" ? s.Below!.Value : s.Above!.Value;
}

/// <summary>
/// Every few seconds: ends sessions whose time is up and raises an alarm when a monitored thermometer goes silent for
/// three intervals (monitor_stale), with an all clear when it speaks again (monitor_resumed).
/// </summary>
public sealed class MonitoringWatch(IServiceScopeFactory scopes, IngestQueue ingest, MqttGateway mqtt, TimeProvider time, ILogger<MonitoringWatch> logger)
    : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    /// <summary>Silence that counts as "stopped": three missed readings, and never less than 20 s (Wi-Fi hiccups, config arriving).</summary>
    public static TimeSpan StaleAfter(int intervalS) => TimeSpan.FromSeconds(Math.Max(20, 3 * intervalS));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
            return;
        using var timer = new PeriodicTimer(Tick, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Monitoring check failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task CheckAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var devices = await db.Devices.Where(d => d.Monitor != null).ToListAsync(ct);
        if (devices.Count == 0)
            return;

        var now = time.GetUtcNow();
        var configs = new List<(string DeviceId, string Config)>();
        foreach (var device in devices)
        {
            if (MonitorState.Read(device) is not { } state)
                continue;
            if (now >= state.Until)
            {
                configs.Add((device.DeviceId, MonitoringService.End(device, state)));
                logger.LogInformation("Monitoring of {Device} ended; tele interval back to {Interval}", device.DeviceId, state.RestoreTeleS);
                continue;
            }

            var since = state.StartedAt;
            var last = await db.Telemetry.AsNoTracking()
                .Where(t => t.DeviceId == device.DeviceId && t.Metric == state.Metric && t.Ts >= since)
                .MaxAsync(t => (DateTimeOffset?)t.ReceivedAt, ct);
            var silent = now - (last ?? since);
            if (state.StaleSince is null && silent > StaleAfter(state.IntervalS))
            {
                MonitorState.Write(device, state with { StaleSince = now });
                ingest.Enqueue(new DeviceEventReceived(device.DeviceId, Ulid.New(now), now, now, "monitor_stale", EventSeverity.Alarm, null,
                    JsonSerializer.SerializeToDocument(new { metric = state.Metric, silentS = (int)silent.TotalSeconds, intervalS = state.IntervalS })));
            }
            else if (state.StaleSince is { } stale && last > stale)
            {
                MonitorState.Write(device, state with { StaleSince = null });
                ingest.Enqueue(new DeviceEventReceived(device.DeviceId, Ulid.New(now), now, now, "monitor_resumed", EventSeverity.Info, null,
                    JsonSerializer.SerializeToDocument(new { metric = state.Metric, silentS = (int)(now - stale).TotalSeconds })));
            }
        }
        await db.SaveChangesAsync(ct);
        if (mqtt.IsConnected)
            foreach (var (deviceId, config) in configs)
                await mqtt.PublishAsync(Topics.Config(deviceId), config, retain: true, ct);
    }
}
