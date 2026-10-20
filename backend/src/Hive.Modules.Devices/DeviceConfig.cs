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

/// <summary>PUT /api/devices/{id}/name: the human name shown everywhere (a typo made when adding the hornet).</summary>
public sealed record RenameDeviceRequest(string Name);

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

    public const int MaxNameLength = 128;

    /// <summary>The thermometer a Bluetooth bridge (type ble) talks to: its address, and whether raw frames go to the log.</summary>
    public sealed record BleRequest(string? Mac = null, bool Dump = false);

    private static readonly System.Text.RegularExpressions.Regex MacRegex = new("^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$");

    public static string? ValidateBle(BleRequest r) =>
        string.IsNullOrEmpty(r.Mac) || MacRegex.IsMatch(r.Mac) ? null : "mac: адрес вида AA:BB:CC:DD:EE:FF";

    /// <summary>Sets config "ble" of a bridge and publishes the config. An empty address makes the bridge scan again.</summary>
    public async Task<ConfigUpdateStatus> SetBleAsync(string deviceId, BleRequest r, string by, CancellationToken ct)
    {
        if (ValidateBle(r) is not null)
            return ConfigUpdateStatus.BadRequest;
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.TypeCode == "ble", ct);
        if (device is null)
            return ConfigUpdateStatus.NotFound;
        var config = device.Config is { } current
            ? JsonNode.Parse(current.RootElement.GetRawText())!.AsObject()
            : new JsonObject { ["v"] = 1, ["name"] = device.Name, ["health_interval_s"] = 60 };
        var ble = new JsonObject();
        if (!string.IsNullOrEmpty(r.Mac))
            ble["mac"] = r.Mac.ToUpperInvariant();
        if (r.Dump)
            ble["dump"] = true;
        if (ble.Count == 0) config.Remove("ble"); else config["ble"] = ble;
        device.ConfigRev++;
        config["rev"] = device.ConfigRev;
        device.Config = JsonDocument.Parse(config.ToJsonString());
        Audit.Record(db, time.GetUtcNow(), by, "device_ble", deviceId, new { mac = r.Mac, r.Dump });
        await db.SaveChangesAsync(ct);
        if (mqtt.IsConnected)
            await mqtt.PublishAsync(Topics.Config(deviceId), config.ToJsonString(), retain: true, ct);
        return ConfigUpdateStatus.Updated;
    }

    /// <summary>The bridge's current "ble" settings.</summary>
    public async Task<BleRequest?> GetBleAsync(string deviceId, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.TypeCode == "ble", ct);
        if (device is null)
            return null;
        if (device.Config is null || !device.Config.RootElement.TryGetProperty("ble", out var ble))
            return new BleRequest();
        return new BleRequest(ble.TryGetProperty("mac", out var m) ? m.GetString() : null, ble.TryGetProperty("dump", out var d) && d.ValueKind == JsonValueKind.True);
    }

    /// <summary>
    /// Renames a hornet. The name also lives in its retained config (contracts: config.name), so a hornet that
    /// already has a config gets a new rev with the new name.
    /// </summary>
    public async Task<ConfigUpdateStatus> RenameAsync(string deviceId, string? name, string by, CancellationToken ct)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxNameLength)
            return ConfigUpdateStatus.BadRequest;
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return ConfigUpdateStatus.NotFound;
        if (device.Name == trimmed)
            return ConfigUpdateStatus.Updated;

        var from = device.Name;
        device.Name = trimmed;
        JsonObject? config = null;
        if (device.Config is { } current)
        {
            config = JsonNode.Parse(current.RootElement.GetRawText())!.AsObject();
            config["name"] = trimmed;
            device.ConfigRev++;
            config["rev"] = device.ConfigRev;
            device.Config = JsonDocument.Parse(config.ToJsonString());
        }
        Audit.Record(db, time.GetUtcNow(), by, "device_rename", deviceId, new { from, to = trimmed });
        await db.SaveChangesAsync(ct);
        if (config is not null && mqtt.IsConnected && device.Status != DeviceStatus.Disabled)
            await mqtt.PublishAsync(Topics.Config(deviceId), config.ToJsonString(), retain: true, ct);
        return ConfigUpdateStatus.Updated;
    }

    /// <summary>
    /// Pins to override, keyed as in the config "hw" section (onewire_pin, sda_pin, …). Null keeps the board default.
    /// VbatRatio is the battery divider ratio (R1+R2)/R2 that turns the ADC voltage into the battery voltage.
    /// </summary>
    public sealed record PinsRequest(int? OnewirePin = null, int? SdaPin = null, int? SclPin = null, int? RelayPin = null, int? ButtonPin = null, int? VbatPin = null, double? VbatRatio = null);

    public sealed record PinsResult(ConfigUpdateStatus Status, string? Error, IReadOnlyList<BoardPins.PinRow> Rows, IReadOnlyList<string> Conflicts, double? VbatRatio = null);

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
        if (r.VbatRatio is { } ratio && !BoardPins.ValidVbatRatio(ratio))
            return new PinsResult(ConfigUpdateStatus.BadRequest, "Коэффициент делителя: больше 0 и не больше 100", [], []);

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
        if (r.VbatRatio is { } vr) hw["vbat_ratio"] = vr;
        else hw.Remove("vbat_ratio");
        var hwElement = JsonDocument.Parse(hw.ToJsonString()).RootElement;
        var features = DeviceFeatures.Effective(device.TypeCode, device.Config?.RootElement);
        var rows = BoardPins.Rows(device.Hw, device.TypeCode, hwElement, features);
        var conflicts = BoardPins.Conflicts(device.TypeCode, rows, device.Hw, features);
        if (conflicts.Count > 0)
            return new PinsResult(ConfigUpdateStatus.BadRequest, "Пины конфликтуют: " + string.Join("; ", conflicts), rows, conflicts);

        if (hw.Count == 0) config.Remove("hw"); else config["hw"] = hw;
        device.ConfigRev++;
        config["rev"] = device.ConfigRev;
        device.Config = JsonDocument.Parse(config.ToJsonString());
        Audit.Record(db, time.GetUtcNow(), by, "device_pins", deviceId, new { r.OnewirePin, r.SdaPin, r.SclPin, r.RelayPin, r.ButtonPin, r.VbatPin, r.VbatRatio });
        await db.SaveChangesAsync(ct);
        if (mqtt.IsConnected)
            await mqtt.PublishAsync(Topics.Config(deviceId), config.ToJsonString(), retain: true, ct);
        return new PinsResult(ConfigUpdateStatus.Updated, null, rows, conflicts, r.VbatRatio);
    }

    /// <summary>The pins a hornet uses now, with where each one comes from, and any conflict.</summary>
    public async Task<PinsResult> GetPinsAsync(string deviceId, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return new PinsResult(ConfigUpdateStatus.NotFound, null, [], []);
        JsonElement? hw = device.Config?.RootElement.TryGetProperty("hw", out var h) == true ? h : null;
        var features = DeviceFeatures.Effective(device.TypeCode, device.Config?.RootElement);
        var rows = BoardPins.Rows(device.Hw, device.TypeCode, hw, features);
        return new PinsResult(ConfigUpdateStatus.Updated, null, rows, BoardPins.Conflicts(device.TypeCode, rows, device.Hw, features), BoardPins.VbatRatio(hw));
    }

    /// <summary>
    /// PUT /api/devices/{id}/features: what the hornet is used for, and for a heater the limits it keeps by itself
    /// (null keeps what the config has, else the firmware defaults). Max on time is in seconds, as in the contract.
    /// </summary>
    public sealed record FeaturesRequest(IReadOnlyList<string> Features, double? MinC = null, double? MaxC = null, int? MaxOnS = null);

    public sealed record FeaturesResult(ConfigUpdateStatus Status, string? Error = null);

    /// <summary>
    /// Sets config "features" (and failsafe.heater for a heater) and publishes the config. Refused when the role's
    /// firmware cannot do a feature, a heater has no sensor, the limits make no sense, or the chosen features would
    /// put two functions on one pin (ESP-01: relay and I2C both on GPIO0).
    /// </summary>
    public async Task<FeaturesResult> SetFeaturesAsync(string deviceId, FeaturesRequest r, string by, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return new FeaturesResult(ConfigUpdateStatus.NotFound);
        var features = DeviceFeatures.Normalize(r.Features ?? []);
        if (DeviceFeatures.Validate(device.TypeCode, features) is { } error)
            return new FeaturesResult(ConfigUpdateStatus.BadRequest, error);

        var config = device.Config is { } current
            ? JsonNode.Parse(current.RootElement.GetRawText())!.AsObject()
            : new JsonObject { ["v"] = 1, ["name"] = device.Name, ["health_interval_s"] = 60 };
        JsonElement? hw = device.Config?.RootElement.TryGetProperty("hw", out var h) == true ? h : null;
        var conflicts = BoardPins.Conflicts(device.TypeCode, BoardPins.Rows(device.Hw, device.TypeCode, hw, features), device.Hw, features);
        if (conflicts.Count > 0)
            return new FeaturesResult(ConfigUpdateStatus.BadRequest, "Пины конфликтуют: " + string.Join("; ", conflicts));

        config["features"] = new JsonArray(features.Select(f => (JsonNode)f).ToArray());
        if (features.Contains(DeviceFeatures.Heater))
        {
            var (minC, maxC, maxOnS) = Heater(device) ?? (DeviceFeatures.DefaultMinC, DeviceFeatures.DefaultMaxC, DeviceFeatures.DefaultMaxOnS);
            minC = r.MinC ?? minC;
            maxC = r.MaxC ?? maxC;
            maxOnS = r.MaxOnS ?? maxOnS;
            if (DeviceFeatures.ValidateHeater(minC, maxC, maxOnS) is { } limits)
                return new FeaturesResult(ConfigUpdateStatus.BadRequest, limits);
            var failsafe = config["failsafe"] as JsonObject ?? new JsonObject();
            failsafe["heater"] = new JsonObject { ["min_c"] = minC, ["max_c"] = maxC, ["max_on_s"] = maxOnS };
            config["failsafe"] = failsafe;
        }
        device.ConfigRev++;
        config["rev"] = device.ConfigRev;
        device.Config = JsonDocument.Parse(config.ToJsonString());
        Audit.Record(db, time.GetUtcNow(), by, "device_features", deviceId, new { features, r.MinC, r.MaxC, r.MaxOnS });
        await db.SaveChangesAsync(ct);
        if (mqtt.IsConnected && device.Status != DeviceStatus.Disabled)
            await mqtt.PublishAsync(Topics.Config(deviceId), config.ToJsonString(), retain: true, ct);
        return new FeaturesResult(ConfigUpdateStatus.Updated);
    }

    /// <summary>The heater limits in the config (failsafe.heater), or null when it has none.</summary>
    public static (double MinC, double MaxC, int MaxOnS)? Heater(Device d) =>
        d.Config?.RootElement.TryGetProperty("failsafe", out var f) == true && f.ValueKind == JsonValueKind.Object
        && f.TryGetProperty("heater", out var h) && h.ValueKind == JsonValueKind.Object
        && h.TryGetProperty("min_c", out var min) && h.TryGetProperty("max_c", out var max) && h.TryGetProperty("max_on_s", out var on)
            ? (min.GetDouble(), max.GetDouble(), on.GetInt32())
            : null;

    public static int? SleepS(Device d) =>
        d.Config?.RootElement.TryGetProperty("sleep_s", out var s) == true && s.TryGetInt32(out var v) && v > 0 ? v : null;

    /// <summary>light or deep; deep when sleep_mode is absent (the contract default).</summary>
    public static string SleepMode(Device d) =>
        d.Config?.RootElement.TryGetProperty("sleep_mode", out var m) == true && m.GetString() == "light" ? "light" : "deep";

    /// <summary>A deep sleeper between wake-ups: awake only for seconds, so a command sent now would not reach it.</summary>
    public static bool Asleep(Device d, DateTimeOffset now) =>
        SleepS(d) is not null && SleepMode(d) == "deep" && d.LastSeenAt is { } seen && now - seen > TimeSpan.FromSeconds(90);
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
