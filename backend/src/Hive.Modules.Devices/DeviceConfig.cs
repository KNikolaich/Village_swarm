using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hive.Modules.Devices;

/// <summary>PATCH /api/devices/{id}/config: the settings a person changes from the swarm screen. Null keeps the value.</summary>
public sealed record UpdateDeviceConfigRequest(int? SleepS = null, int? TeleIntervalS = null, Hive.Contracts.Messages.SleepMode? SleepMode = null);

public enum ConfigUpdateStatus { Updated, NotFound, BadRequest }

/// <summary>
/// Changes a hornet's retained config (contracts/schemas/config.schema.json): bumps rev, stores it and publishes it.
/// A sleeping hornet picks it up at its next wake-up, an awake one at once.
/// </summary>
public sealed class DeviceConfigService(HiveDbContext db, MqttGateway mqtt, TimeProvider time)
{
    public const int MinSleepS = 60;
    public const int MaxSleepS = 86400;

    public static string? Validate(UpdateDeviceConfigRequest r)
    {
        if (r.SleepS is null && r.TeleIntervalS is null && r.SleepMode is null)
            return "Nothing to change";
        if (r.SleepS is { } s && s != 0 && s is < MinSleepS or > MaxSleepS)
            return $"sleepS must be 0 or between {MinSleepS} and {MaxSleepS}";
        if (r.TeleIntervalS is { } t && t is < 10 or > 86400)
            return "teleIntervalS must be between 10 and 86400";
        return null;
    }

    public async Task<ConfigUpdateStatus> UpdateAsync(string deviceId, UpdateDeviceConfigRequest r, string by, CancellationToken ct)
    {
        if (Validate(r) is not null)
            return ConfigUpdateStatus.BadRequest;
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return ConfigUpdateStatus.NotFound;

        var config = device.Config is { } current
            ? JsonNode.Parse(current.RootElement.GetRawText())!.AsObject()
            : new JsonObject { ["v"] = 1, ["name"] = device.Name, ["health_interval_s"] = 60 };
        if (r.SleepS is { } sleep)
        {
            if (sleep == 0)
                config.Remove("sleep_s");
            else
                config["sleep_s"] = sleep;
        }
        if (r.TeleIntervalS is { } tele)
            config["tele_interval_s"] = tele;
        if (r.SleepMode is { } mode)
            config["sleep_mode"] = mode == Hive.Contracts.Messages.SleepMode.Light ? "light" : "deep";
        device.ConfigRev++;
        config["rev"] = device.ConfigRev;
        device.Config = JsonDocument.Parse(config.ToJsonString());

        Audit.Record(db, time.GetUtcNow(), by, "device_config", deviceId, new { r.SleepS, r.TeleIntervalS, r.SleepMode });
        await db.SaveChangesAsync(ct);
        if (mqtt.IsConnected)
            await mqtt.PublishAsync(Topics.Config(deviceId), config.ToJsonString(), retain: true, ct);
        return ConfigUpdateStatus.Updated;
    }

    /// <summary>Pins to override, keyed as in the config "hw" section (onewire_pin, sda_pin, …). Null keeps the board default.</summary>
    public sealed record PinsRequest(int? OnewirePin = null, int? SdaPin = null, int? SclPin = null, int? RelayPin = null, int? ButtonPin = null, int? VbatPin = null);

    public sealed record PinsResult(ConfigUpdateStatus Status, string? Error, IReadOnlyList<BoardPins.PinRow> Rows, IReadOnlyList<string> Conflicts);

    /// <summary>
    /// Sets pins by hand (where a sensor is wired is not always the default). Rejected when a pin is out of range or
    /// two functions of the hornet's role end up on the same pin. The new "hw" goes out as the retained config.
    /// </summary>
    public async Task<PinsResult> SetPinsAsync(string deviceId, PinsRequest r, string by, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return new PinsResult(ConfigUpdateStatus.NotFound, null, [], []);
        foreach (var pin in new[] { r.OnewirePin, r.SdaPin, r.SclPin, r.RelayPin, r.ButtonPin, r.VbatPin })
            if (pin is { } p && !BoardPins.ValidPin(p))
                return new PinsResult(ConfigUpdateStatus.BadRequest, "pin: от 0 до 39", [], []);

        var config = device.Config is { } current
            ? JsonNode.Parse(current.RootElement.GetRawText())!.AsObject()
            : new JsonObject { ["v"] = 1, ["name"] = device.Name, ["health_interval_s"] = 60 };
        var hw = config["hw"] as JsonObject ?? new JsonObject();
        void Set(string key, int? value)
        {
            if (value is { } v) hw[key] = v;
            else hw.Remove(key);
        }
        Set("onewire_pin", r.OnewirePin);
        Set("sda_pin", r.SdaPin);
        Set("scl_pin", r.SclPin);
        Set("relay_pin", r.RelayPin);
        Set("button_pin", r.ButtonPin);
        Set("vbat_pin", r.VbatPin);
        var hwElement = JsonDocument.Parse(hw.ToJsonString()).RootElement;
        var rows = BoardPins.Rows(device.Hw, device.TypeCode, hwElement);
        var conflicts = BoardPins.Conflicts(device.TypeCode, rows);
        if (conflicts.Count > 0)
            return new PinsResult(ConfigUpdateStatus.BadRequest, "Пины конфликтуют: " + string.Join("; ", conflicts), rows, conflicts);

        if (hw.Count == 0) config.Remove("hw"); else config["hw"] = hw;
        device.ConfigRev++;
        config["rev"] = device.ConfigRev;
        device.Config = JsonDocument.Parse(config.ToJsonString());
        Audit.Record(db, time.GetUtcNow(), by, "device_pins", deviceId, new { r.OnewirePin, r.SdaPin, r.SclPin, r.RelayPin, r.ButtonPin, r.VbatPin });
        await db.SaveChangesAsync(ct);
        if (mqtt.IsConnected)
            await mqtt.PublishAsync(Topics.Config(deviceId), config.ToJsonString(), retain: true, ct);
        return new PinsResult(ConfigUpdateStatus.Updated, null, rows, conflicts);
    }

    /// <summary>The pins a hornet uses now, with where each one comes from, and any conflict.</summary>
    public async Task<PinsResult> GetPinsAsync(string deviceId, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return new PinsResult(ConfigUpdateStatus.NotFound, null, [], []);
        JsonElement? hw = device.Config?.RootElement.TryGetProperty("hw", out var h) == true ? h : null;
        var rows = BoardPins.Rows(device.Hw, device.TypeCode, hw);
        return new PinsResult(ConfigUpdateStatus.Updated, null, rows, BoardPins.Conflicts(device.TypeCode, rows));
    }

    public static int? SleepS(Device d) =>
        d.Config?.RootElement.TryGetProperty("sleep_s", out var s) == true && s.TryGetInt32(out var v) && v > 0 ? v : null;

    /// <summary>light or deep; deep when sleep_mode is absent (the contract default).</summary>
    public static string SleepMode(Device d) =>
        d.Config?.RootElement.TryGetProperty("sleep_mode", out var m) == true && m.GetString() == "light" ? "light" : "deep";
}

/// <summary>
/// A sleeping hornet disconnects cleanly, so no LWT tells the hive when its battery dies. It is marked offline
/// once it misses several wake-ups (contracts/mqtt-topics.md "Sleeping hornets").
/// </summary>
public sealed class SleepingDeviceWatch(IServiceScopeFactory scopes, TimeProvider time, ILogger<SleepingDeviceWatch> logger) : BackgroundService
{
    public static bool IsOverdue(int sleepS, DateTimeOffset? lastSeenAt, DateTimeOffset now) =>
        lastSeenAt is { } seen && now - seen > TimeSpan.FromSeconds(3L * sleepS + 60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Sleeping device check failed");
            }
        }
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var now = time.GetUtcNow();
        var online = await db.Devices.Where(d => d.Status == DeviceStatus.Online && d.Config != null).ToListAsync(ct);
        foreach (var device in online)
        {
            // Light sleepers stay connected and have the LWT; only deep sleepers need this check.
            if (DeviceConfigService.SleepS(device) is { } sleep && DeviceConfigService.SleepMode(device) == "deep"
                && IsOverdue(sleep, device.LastSeenAt, now))
            {
                device.Status = DeviceStatus.Offline;
                logger.LogInformation("{Device} missed its wake-ups (sleep {Sleep} s, last seen {Seen}); marked offline", device.DeviceId, sleep, device.LastSeenAt);
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
