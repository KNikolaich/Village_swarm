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

public sealed record EnrollRequest(string Code, string Mac, string? Hw = null, string? Fw = null);

public sealed record EnrollMqtt(string Host, int Port, string? User, string? Pass);

public sealed record EnrollResponse(string DeviceId, EnrollMqtt Mqtt, string UploadUrl, string UploadToken, JsonElement Config);

public enum EnrollStatus { Ok, BadCode, BadRequest }

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

    public async Task<(EnrollmentCodeDto? Code, string? Error)> CreateCodeAsync(CreateCodeRequest r, string by, CancellationToken ct)
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
            ExpiresAt = now.AddMinutes(options.Value.CodeMinutes),
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

/// <summary>Sets dynsec defaults whenever the api (re)connects to the broker.</summary>
public sealed class DynamicSecurityBootstrap(MqttGateway mqtt, DynamicSecurity dynsec) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        mqtt.Connected += dynsec.EnsureDefaultsAsync;
        // The gateway may have connected before this hook was added.
        return mqtt.IsConnected ? dynsec.EnsureDefaultsAsync(cancellationToken) : Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        mqtt.Connected -= dynsec.EnsureDefaultsAsync;
        return Task.CompletedTask;
    }
}
