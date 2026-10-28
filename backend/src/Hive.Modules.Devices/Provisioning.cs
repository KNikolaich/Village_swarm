using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Mqtt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Devices;

public sealed class ProvisioningOptions
{
    public const string Section = "Provisioning";

    /// <summary>Broker address handed to hornets. Empty: the host the hornet used to reach the api.</summary>
    public string? MqttHost { get; set; }

    public int MqttPort { get; set; } = 1883;

    /// <summary>Photo upload base URL for hornets (spec 13.2: the 8443 ingest port). Empty: the enroll request's base URL.</summary>
    public string? UploadUrl { get; set; }

    /// <summary>Folder with flashable images per type: {FirmwareDir}/{type}/manifest.json and *.bin.</summary>
    public string FirmwareDir { get; set; } = "/srv/hive/firmware";

    public int CodeMinutes { get; set; } = 30;
}

public sealed record CreateCodeRequest(string DeviceId, string Type, string Name, int? ZoneId = null);

public sealed record EnrollmentCodeDto(string Code, string DeviceId, string Type, string Name, DateTimeOffset ExpiresAt, bool Replaces);

/// <summary>A batch of N hornets of one type: ids <c>{IdPrefix}1…{IdPrefix}N</c>, names "{NamePrefix} 1…N", one code each.</summary>
public sealed record CreateBatchRequest(string Type, string IdPrefix, string NamePrefix, int Count, int? Minutes = null);

public sealed record BatchDto(IReadOnlyList<EnrollmentCodeDto> Items);

public sealed record EnrollRequest(string Code, string Mac, string? Hw = null, string? Fw = null);

public sealed record EnrollMqtt(string Host, int Port, string? User, string? Pass);

public sealed record EnrollResponse(string DeviceId, EnrollMqtt Mqtt, string UploadUrl, string UploadToken, JsonElement Config);

/// <summary>A Tasmota or OpenBeken device. MaxOnMinutes is the longest a heater may stay switched on without a new order.</summary>
public sealed record CreateTasmotaRequest(string DeviceId, string Type, string Name, int? ZoneId = null, int? MaxOnMinutes = null, bool Ir = false);

/// <summary>Console lines to paste into the device's Tasmota web console, in this order. The password is shown once and kept nowhere.</summary>
public sealed record TasmotaSetup(string DeviceId, EnrollMqtt Mqtt, IReadOnlyList<string> Lines);

public enum EnrollStatus { Ok, BadCode, BadRequest }

public enum PurgeStatus { Purged, NotFound, WasSeen }

/// <summary>Adding a hornet (spec 5.3): one-time code in the UI, the board trades it for its credentials.</summary>
public sealed class ProvisioningService(
    HiveDbContext db,
    DynamicSecurity dynsec,
    MqttGateway mqtt,
    IOptions<ProvisioningOptions> options,
    TimeProvider time)
{
    // No 0/O, 1/I: typed from a screen.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public const int MaxBatch = 20;

    /// <summary>
    /// Codes for a whole batch, kept on the server so closing the wizard loses nothing. The lifetime is longer than
    /// a single code's (a batch is flashed and configured board by board over hours); 30 to 1440 minutes.
    /// </summary>
    public async Task<(BatchDto? Batch, string? Error)> CreateBatchAsync(CreateBatchRequest r, string by, CancellationToken ct)
    {
        if (r.Count is < 1 or > MaxBatch)
            return (null, $"count: от 1 до {MaxBatch}");
        if (string.IsNullOrWhiteSpace(r.NamePrefix))
            return (null, "namePrefix is required");
        var minutes = Math.Clamp(r.Minutes ?? 240, 30, 1440);
        var items = new List<EnrollmentCodeDto>();
        for (var i = 1; i <= r.Count; i++)
        {
            var (code, error) = await CreateCodeAsync(new CreateCodeRequest($"{r.IdPrefix}{i}", r.Type, $"{r.NamePrefix} {i}"), by, ct, minutes);
            if (code is null)
                return (null, error);
            items.Add(code);
        }
        return (new BatchDto(items), null);
    }

    /// <summary>Codes that are not used yet and not expired: the batch queue shown in the wizard.</summary>
    public async Task<IReadOnlyList<EnrollmentCodeDto>> PendingCodesAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var rows = await db.EnrollmentCodes.AsNoTracking()
            .Where(c => c.UsedAt == null && c.ExpiresAt > now)
            .OrderBy(c => c.DeviceId).ToListAsync(ct);
        return rows.Select(c => new EnrollmentCodeDto(c.Code, c.DeviceId, c.TypeCode, c.Name, c.ExpiresAt, false)).ToList();
    }

    public async Task<(EnrollmentCodeDto? Code, string? Error)> CreateCodeAsync(CreateCodeRequest r, string by, CancellationToken ct, int? minutes = null)
    {
        if (!Topics.IsValidDeviceId(r.DeviceId))
            return (null, "device_id: латиница в нижнем регистре, цифры и дефисы, например guard-gate1");
        if (!await db.DeviceTypes.AnyAsync(t => t.Code == r.Type, ct))
            return (null, $"unknown type {r.Type}");
        if (string.IsNullOrWhiteSpace(r.Name))
            return (null, "name is required");

        var now = time.GetUtcNow();
        var code = new EnrollmentCode
        {
            Code = RandomNumberGenerator.GetString(CodeAlphabet, 8),
            DeviceId = r.DeviceId,
            TypeCode = r.Type,
            Name = r.Name.Trim(),
            ZoneId = r.ZoneId,
            ExpiresAt = now.AddMinutes(minutes ?? options.Value.CodeMinutes),
            CreatedBy = by,
        };
        db.EnrollmentCodes.Add(code);
        var replaces = await db.Devices.AnyAsync(d => d.DeviceId == r.DeviceId, ct);
        Audit.Record(db, now, by, replaces ? "device_replace_code" : "device_add_code", r.DeviceId);
        await db.SaveChangesAsync(ct);
        return (new EnrollmentCodeDto(code.Code, code.DeviceId, code.TypeCode, code.Name, code.ExpiresAt, replaces), null);
    }

    /// <summary>
    /// Called by the hornet with its code: creates or replaces the device, its broker login (dynsec) and
    /// upload token, publishes its retained config. The same device_id keeps its history (spec 5.3 "Заменить").
    /// </summary>
    public async Task<(EnrollStatus Status, EnrollResponse? Response)> EnrollAsync(EnrollRequest r, Uri requestBase, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Mac))
            return (EnrollStatus.BadRequest, null);
        var now = time.GetUtcNow();
        var code = await db.EnrollmentCodes.FirstOrDefaultAsync(c => c.Code == r.Code.Trim().ToUpperInvariant() && c.UsedAt == null && c.ExpiresAt > now, ct);
        if (code is null)
            return (EnrollStatus.BadCode, null);

        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == code.DeviceId, ct);
        if (device is null)
        {
            device = new Device { DeviceId = code.DeviceId, TypeCode = code.TypeCode, Name = code.Name, CreatedAt = now };
            db.Devices.Add(device);
        }
        device.TypeCode = code.TypeCode;
        device.Name = code.Name;
        device.ZoneId = code.ZoneId ?? device.ZoneId;
        device.Mac = r.Mac.ToUpperInvariant();
        device.Hw = r.Hw ?? device.Hw;
        device.FwVersion = r.Fw ?? device.FwVersion;
        device.Status = DeviceStatus.Pending;

        var mqttPassword = Secret();
        var uploadToken = Secret();
        device.MqttUser = dynsec.Enabled ? DynamicSecurity.ClientName(device.DeviceId) : null;
        device.UploadTokenHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uploadToken)));

        var uploadUrl = (options.Value.UploadUrl ?? requestBase.GetLeftPart(UriPartial.Authority)).TrimEnd('/');
        var config = await InitialConfigAsync(device, uploadUrl, ct);
        device.ConfigRev++;
        config["rev"] = device.ConfigRev;
        device.Config = JsonDocument.Parse(config.ToJsonString());

        code.UsedAt = now;
        code.UsedByMac = device.Mac;
        Audit.Record(db, now, $"device:{device.DeviceId}", "device_enrolled", device.DeviceId, new { device.Mac, r.Fw });

        await dynsec.ProvisionHornetAsync(device.DeviceId, mqttPassword, ct);
        await db.SaveChangesAsync(ct);
        if (mqtt.IsConnected)
            await mqtt.PublishAsync(Topics.Config(device.DeviceId), config.ToJsonString(), retain: true, ct);

        var host = string.IsNullOrEmpty(options.Value.MqttHost) ? requestBase.Host : options.Value.MqttHost;
        return (EnrollStatus.Ok, new EnrollResponse(
            device.DeviceId,
            new EnrollMqtt(host, options.Value.MqttPort, device.MqttUser, dynsec.Enabled ? mqttPassword : null),
            uploadUrl,
            uploadToken,
            JsonSerializer.SerializeToElement(config)));
    }

    /// <summary>
    /// Adds a device that runs Tasmota or OpenBeken (contracts/tasmota.md): it gets its own broker login limited to its
    /// topics, and the Tasmota console lines that point it at the hive. Asking again for the same id gives new credentials
    /// (the board was replaced, or the old login is lost).
    /// </summary>
    public async Task<(TasmotaSetup? Setup, string? Error)> CreateTasmotaAsync(CreateTasmotaRequest r, string by, Uri requestBase, CancellationToken ct)
    {
        if (!Topics.IsValidDeviceId(r.DeviceId))
            return (null, "device_id: латиница в нижнем регистре, цифры и дефисы, например heat-bedroom1");
        if (!await db.DeviceTypes.AnyAsync(t => t.Code == r.Type, ct))
            return (null, $"unknown type {r.Type}");
        if (string.IsNullOrWhiteSpace(r.Name))
            return (null, "name is required");

        var now = time.GetUtcNow();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == r.DeviceId, ct);
        if (device is not null && device.Driver != Drivers.Tasmota && device.Status != DeviceStatus.Disabled)
            return (null, $"{r.DeviceId} уже занят шершнем на нашей прошивке; выберите другой id");
        if (device is null)
        {
            device = new Device { DeviceId = r.DeviceId, TypeCode = r.Type, Name = r.Name.Trim(), CreatedAt = now };
            db.Devices.Add(device);
        }
        device.TypeCode = r.Type;
        device.Name = r.Name.Trim();
        device.ZoneId = r.ZoneId ?? device.ZoneId;
        device.Driver = Drivers.Tasmota;
        device.Hw = "tasmota";
        device.Status = DeviceStatus.Pending;
        device.MqttUser = DynamicSecurity.ClientName(device.DeviceId);
        device.UploadTokenHash = null;
        var switches = r.Type is "relay" or "heat";
        device.Info = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            caps = (switches ? new[] { "relay" } : Array.Empty<string>()).Concat(r.Ir ? ["ir"] : []).ToArray(),
            metrics = Array.Empty<string>(),
            commands = (switches ? new[] { "relay", "reboot" } : ["reboot"]).Concat(r.Ir ? ["ir"] : []).ToArray(),
        }));
        Audit.Record(db, now, by, "device_add_tasmota", r.DeviceId);

        var password = Secret();
        await dynsec.ProvisionTasmotaAsync(device.DeviceId, password, ct);
        await db.SaveChangesAsync(ct);

        var host = string.IsNullOrEmpty(options.Value.MqttHost) ? requestBase.Host : options.Value.MqttHost;
        var port = options.Value.MqttPort;
        var lines = new List<string> { $"Backlog Topic {r.DeviceId}; DeviceName {r.DeviceId}; FriendlyName1 {r.DeviceId}" };
        // A relay comes up switched off after a power cut, and reports every minute so the hive sees it is alive.
        lines.Add("Backlog TelePeriod 60; SetOption19 0" + (switches ? "; PowerOnState 0; PowerRetain 0" : ""));
        if (r.Type == "heat")
        {
            // The relay switches itself off after this long, even when the hive is gone (PulseTime: seconds plus 100).
            var seconds = Math.Clamp(r.MaxOnMinutes ?? 360, 1, 1080) * 60;
            lines.Add($"PulseTime1 {seconds + 100}");
        }
        lines.Add($"Backlog MqttHost {host}; MqttPort {port}; MqttUser {device.MqttUser}; MqttPassword {password}; MqttClient {r.DeviceId}");
        return (new TasmotaSetup(device.DeviceId, new EnrollMqtt(host, port, device.MqttUser, dynsec.Enabled ? password : null), lines), null);
    }

    /// <summary>DELETE /api/devices/{id}: revokes the broker login and upload token, keeps the history.</summary>
    public async Task<bool> DisableAsync(string deviceId, string by, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return false;
        device.Status = DeviceStatus.Disabled;
        device.UploadTokenHash = null;
        device.MqttUser = null;
        Audit.Record(db, time.GetUtcNow(), by, "device_disabled", deviceId);
        await dynsec.RevokeHornetAsync(deviceId, ct);
        await db.SaveChangesAsync(ct);
        if (mqtt.IsConnected)
            await mqtt.PublishAsync(Topics.Config(deviceId), "", retain: true, ct); // clear retained config
        return true;
    }

    /// <summary>
    /// Removes a device that never reached the hive (a flash that failed half way, a wrong id) for good, so its id is free
    /// again. One that has been seen has history and stays in the archive (<see cref="DisableAsync"/>).
    /// </summary>
    public async Task<PurgeStatus> PurgeAsync(string deviceId, string by, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return PurgeStatus.NotFound;
        if (device.LastSeenAt is not null)
            return PurgeStatus.WasSeen;
        db.EnrollmentCodes.RemoveRange(db.EnrollmentCodes.Where(c => c.DeviceId == deviceId));
        db.DisplayImages.RemoveRange(db.DisplayImages.Where(i => i.DeviceId == deviceId));
        db.Devices.Remove(device);
        Audit.Record(db, time.GetUtcNow(), by, "device_purged", deviceId);
        var hornet = device.Driver == Drivers.Hornet;
        if (hornet)
            await dynsec.RevokeHornetAsync(deviceId, ct);
        await db.SaveChangesAsync(ct);
        if (hornet && mqtt.IsConnected)
            await mqtt.PublishAsync(Topics.Config(deviceId), "", retain: true, ct);
        return PurgeStatus.Purged;
    }

    /// <summary>Type defaults (device_types.default_config) plus what every hornet needs (spec 4.4 config).</summary>
    private async Task<JsonObject> InitialConfigAsync(Device device, string uploadUrl, CancellationToken ct)
    {
        var type = await db.DeviceTypes.AsNoTracking().FirstAsync(t => t.Code == device.TypeCode, ct);
        var config = type.DefaultConfig is { } d ? JsonNode.Parse(d.RootElement.GetRawText())!.AsObject() : [];
        config["v"] = 1;
        config["name"] = device.Name;
        config["health_interval_s"] ??= 60;
        if (device.TypeCode == "guard-cam")
        {
            var armed = await db.Modes.AsNoTracking().Where(m => m.Key == Mode.Armed).Select(m => m.Value).FirstOrDefaultAsync(ct) == "true";
            config["guard"] ??= new JsonObject { ["cooldown_s"] = 20, ["record_when_disarmed"] = false };
            config["guard"]!["armed"] = armed;
            config["camera"] ??= new JsonObject { ["frame"] = "SVGA", ["quality"] = 12, ["burst"] = 3, ["burst_interval_ms"] = 700 };
        }
        config["upload"] = new JsonObject { ["primary"] = uploadUrl };
        return config;
    }

    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Sets dynsec defaults whenever the api (re)connects to the broker. A broker busy starting up may drop the answer (or the
/// connection): that is logged and tried again in the background rather than stopping the api from starting.
/// </summary>
public sealed class DynamicSecurityBootstrap(MqttGateway mqtt, DynamicSecurity dynsec, ILogger<DynamicSecurityBootstrap> logger) : IHostedService
{
    private readonly CancellationTokenSource _stop = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        mqtt.Connected += dynsec.EnsureDefaultsAsync;
        // The gateway may have connected before this hook was added.
        if (!mqtt.IsConnected)
            return;
        try
        {
            await dynsec.EnsureDefaultsAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("The broker did not answer the dynamic security defaults at start-up; trying again in the background");
            _ = RetryAsync(_stop.Token);
        }
    }

    private async Task RetryAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                await dynsec.EnsureDefaultsAsync(ct);
                return;
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Dynamic security defaults failed");
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        mqtt.Connected -= dynsec.EnsureDefaultsAsync;
        await _stop.CancelAsync();
    }
}
