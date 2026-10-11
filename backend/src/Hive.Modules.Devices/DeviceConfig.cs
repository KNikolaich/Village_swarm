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
public sealed record UpdateDeviceConfigRequest(int? SleepS = null, int? TeleIntervalS = null);

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
        if (r.SleepS is null && r.TeleIntervalS is null)
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
        device.ConfigRev++;
        config["rev"] = device.ConfigRev;
        device.Config = JsonDocument.Parse(config.ToJsonString());

        Audit.Record(db, time.GetUtcNow(), by, "device_config", deviceId, new { r.SleepS, r.TeleIntervalS });
        await db.SaveChangesAsync(ct);
        if (mqtt.IsConnected)
            await mqtt.PublishAsync(Topics.Config(deviceId), config.ToJsonString(), retain: true, ct);
        return ConfigUpdateStatus.Updated;
    }

    public static int? SleepS(Device d) =>
        d.Config?.RootElement.TryGetProperty("sleep_s", out var s) == true && s.TryGetInt32(out var v) && v > 0 ? v : null;
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
            if (DeviceConfigService.SleepS(device) is { } sleep && IsOverdue(sleep, device.LastSeenAt, now))
            {
                device.Status = DeviceStatus.Offline;
                logger.LogInformation("{Device} missed its wake-ups (sleep {Sleep} s, last seen {Seen}); marked offline", device.DeviceId, sleep, device.LastSeenAt);
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
