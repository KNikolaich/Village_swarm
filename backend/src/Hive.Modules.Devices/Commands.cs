using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Contracts;
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

public sealed record SendCommandRequest(string Name, JsonObject? Payload = null, int? TtlS = null);

public sealed record CommandDto(
    string Cid, string DeviceId, string Name, string Status, string IssuedBy,
    DateTimeOffset IssuedAt, DateTimeOffset? AckAt, JsonElement? Ack)
{
    public static CommandDto From(DeviceCommand c) => new(
        c.Cid, c.DeviceId, c.Name, c.Status.ToString().ToLowerInvariant(), c.IssuedBy, c.IssuedAt, c.AckAt,
        c.AckPayload?.RootElement.Clone());
}

public sealed record ArmedDto(bool Armed, DateTimeOffset UpdatedAt, string? UpdatedBy, int Cameras);

public sealed record SetArmedRequest(bool Armed);

public enum SendStatus
{
    Sent,
    UnknownDevice,
    BadRequest,
    BrokerDown,
}

/// <summary>Sends commands over MQTT (spec 4.4 cmd, 6.3 CommandDispatcher): stored first, ack updates it later.</summary>
public sealed class CommandService(HiveDbContext db, MqttGateway mqtt, TimeProvider time)
{
    public const int DefaultTtlS = 60;

    public async Task<(SendStatus Status, DeviceCommand? Command)> SendAsync(
        string deviceId, string name, JsonObject? payload, string issuedBy, int? ttlS, CancellationToken ct)
    {
        if (!Topics.IsValidSegment(name))
            return (SendStatus.BadRequest, null);
        if (!await db.Devices.AnyAsync(d => d.DeviceId == deviceId && d.Status != DeviceStatus.Disabled, ct))
            return (SendStatus.UnknownDevice, null);
        if (!mqtt.IsConnected)
            return (SendStatus.BrokerDown, null);

        var now = time.GetUtcNow();
        var cid = Ulid.New(now);
        var ttl = Math.Clamp(ttlS ?? DefaultTtlS, 1, 3600);
        // Envelope fields first, then the command-specific ones (contracts/schemas/cmd*.schema.json).
        var message = new JsonObject
        {
            ["v"] = 1,
            ["cid"] = cid,
            ["ts"] = now.ToUnixTimeMilliseconds(),
            ["ttl_s"] = ttl,
            ["by"] = issuedBy,
        };
        foreach (var (key, value) in payload ?? [])
            if (key is not ("v" or "cid" or "ts" or "ttl_s" or "by"))
                message[key] = value?.DeepClone();

        var command = new DeviceCommand
        {
            Cid = cid,
            DeviceId = deviceId,
            Name = name,
            Payload = JsonDocument.Parse(message.ToJsonString()),
            IssuedBy = issuedBy,
            IssuedAt = now,
            TtlS = ttl,
        };
        db.Commands.Add(command);
        await db.SaveChangesAsync(ct);
        await mqtt.PublishAsync(Topics.Cmd(deviceId, name), message.ToJsonString(), retain: false, ct);
        return (SendStatus.Sent, command);
    }

    public async Task<DeviceCommand?> GetAsync(string deviceId, string cid, CancellationToken ct) =>
        await db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.Cid == cid && c.DeviceId == deviceId, ct);
}

/// <summary>Guard mode (spec 6.5 /api/modes/armed): stored, and sent to every camera as cmd/arm.</summary>
public sealed class ArmService(HiveDbContext db, CommandService commands, TimeProvider time)
{
    public async Task<ArmedDto> GetAsync(CancellationToken ct)
    {
        var mode = await db.Modes.AsNoTracking().FirstAsync(m => m.Key == Mode.Armed, ct);
        var cameras = await db.Devices.CountAsync(d => d.TypeCode == "guard-cam" && d.Status != DeviceStatus.Disabled, ct);
        return new ArmedDto(mode.Value == "true", mode.UpdatedAt, mode.UpdatedBy, cameras);
    }

    public async Task<ArmedDto> SetAsync(bool armed, string by, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var mode = await db.Modes.FirstAsync(m => m.Key == Mode.Armed, ct);
        mode.Value = armed ? "true" : "false";
        mode.UpdatedAt = now;
        mode.UpdatedBy = by;
        Audit.Record(db, now, by, armed ? "arm" : "disarm");
        await db.SaveChangesAsync(ct);

        // Online cameras switch now. Offline ones keep their last state until they are back
        // (retained config with guard.armed comes with device configuration, spec 6.5 PATCH /api/devices).
        var cameras = await db.Devices.Where(d => d.TypeCode == "guard-cam" && d.Status == DeviceStatus.Online)
            .Select(d => d.DeviceId).ToListAsync(ct);
        foreach (var camera in cameras)
            await commands.SendAsync(camera, "arm", new JsonObject { ["armed"] = armed }, by, ttlS: 300, ct);
        return await GetAsync(ct);
    }
}

/// <summary>Commands without an ack within ttl + 10 s become "timeout".</summary>
public sealed class CommandTimeoutJob(IServiceScopeFactory scopes, TimeProvider time, ILogger<CommandTimeoutJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
                var now = time.GetUtcNow();
                var candidates = await db.Commands.Where(c => c.Status == CommandStatus.Sent && c.IssuedAt < now.AddSeconds(-10)).ToListAsync(stoppingToken);
                foreach (var c in candidates.Where(c => c.IssuedAt.AddSeconds(c.TtlS + 10) < now))
                    c.Status = CommandStatus.Timeout;
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Command timeout check failed");
            }
        }
    }
}
