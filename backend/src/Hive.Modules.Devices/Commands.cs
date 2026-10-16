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

/// <summary>Mode is "disarmed", "armed" or "arriving" (armed while the owners are on their way, spec 6.6.2).</summary>
public sealed record ArmedDto(bool Armed, string Mode, DateTimeOffset UpdatedAt, string? UpdatedBy, int Cameras);

/// <summary>Mode, when given, wins over Armed: "arriving" arms every camera and sets presence to arriving.</summary>
public sealed record SetArmedRequest(bool Armed, string? Mode = null);

public enum SendStatus
{
    Sent,
    UnknownDevice,
    BadRequest,
    BrokerDown,
}

/// <summary>Sends commands over MQTT (spec 4.4 cmd, 6.3 CommandDispatcher): stored first, ack updates it later.</summary>
public sealed class CommandService(HiveDbContext db, MqttGateway mqtt, TasmotaPending tasmota, TimeProvider time)
{
    public const int DefaultTtlS = 60;

    public async Task<(SendStatus Status, DeviceCommand? Command)> SendAsync(
        string deviceId, string name, JsonObject? payload, string issuedBy, int? ttlS, CancellationToken ct)
    {
        if (!Topics.IsValidSegment(name))
            return (SendStatus.BadRequest, null);
        var driver = await db.Devices.Where(d => d.DeviceId == deviceId && d.Status != DeviceStatus.Disabled)
            .Select(d => d.Driver).FirstOrDefaultAsync(ct);
        if (driver is null)
            return (SendStatus.UnknownDevice, null);
        // A Tasmota device speaks its own commands: only the ones that map to them (relay, reboot) are accepted.
        var plan = driver == Drivers.Tasmota ? TasmotaCommands.Plan(name, payload) : null;
        if (driver == Drivers.Tasmota && plan is null)
            return (SendStatus.BadRequest, null);
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
            Payload = JsonDocument.Parse(Redacted(name, message).ToJsonString()),
            IssuedBy = issuedBy,
            IssuedAt = now,
            TtlS = ttl,
        };
        db.Commands.Add(command);
        await db.SaveChangesAsync(ct);
        if (plan is null)
        {
            await mqtt.PublishAsync(Topics.Cmd(deviceId, name), message.ToJsonString(), retain: false, ct);
            return (SendStatus.Sent, command);
        }
        tasmota.Expect(deviceId, plan.Confirms, cid);
        foreach (var call in plan.Calls)
            await mqtt.PublishAsync(TasmotaTopics.Cmnd(deviceId, call.Command), call.Payload, retain: false, ct);
        return (SendStatus.Sent, command);
    }

    /// <summary>Newest commands of one device first (the history on the device page).</summary>
    public async Task<IReadOnlyList<DeviceCommand>> ListAsync(string deviceId, int? limit, CancellationToken ct) =>
        await db.Commands.AsNoTracking().Where(c => c.DeviceId == deviceId)
            .OrderByDescending(c => c.IssuedAt).Take(Math.Clamp(limit ?? 20, 1, 100)).ToListAsync(ct);

    /// <summary>
    /// What the journal keeps of a message: a Wi-Fi password is sent to the hornet but never stored (the journal is read
    /// by more people than the hornet), so cmd/wifi is stored with "***" in its place.
    /// </summary>
    internal static JsonObject Redacted(string name, JsonObject message)
    {
        if (name != "wifi" || !message.ContainsKey("pass"))
            return message;
        var copy = (JsonObject)message.DeepClone();
        copy["pass"] = "***";
        return copy;
    }

    public async Task<DeviceCommand?> GetAsync(string deviceId, string cid, CancellationToken ct) =>
        await db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.Cid == cid && c.DeviceId == deviceId, ct);
}

/// <summary>Guard mode (spec 6.5 /api/modes/armed): stored, and sent to every camera as cmd/arm.
/// "Arriving" keeps the cameras armed and records presence = arriving (spec 6.6.1) so rules can tell it apart.</summary>
public sealed class ArmService(HiveDbContext db, CommandService commands, TimeProvider time)
{
    public const string Disarmed = "disarmed";
    public const string ArmedMode = "armed";
    public const string Arriving = "arriving";

    public static bool IsKnown(string mode) => mode is Disarmed or ArmedMode or Arriving;

    public async Task<ArmedDto> GetAsync(CancellationToken ct)
    {
        var mode = await db.Modes.AsNoTracking().FirstAsync(m => m.Key == Mode.Armed, ct);
        var presence = await db.Modes.AsNoTracking().Where(m => m.Key == Mode.Presence).Select(m => m.Value).FirstOrDefaultAsync(ct);
        var cameras = await db.Devices.CountAsync(d => d.TypeCode == "guard-cam" && d.Status != DeviceStatus.Disabled, ct);
        var armed = mode.Value == "true";
        var name = !armed ? Disarmed : presence == Mode.PresenceArriving ? Arriving : ArmedMode;
        return new ArmedDto(armed, name, mode.UpdatedAt, mode.UpdatedBy, cameras);
    }

    public Task<ArmedDto> SetAsync(bool armed, string by, CancellationToken ct) =>
        SetModeAsync(armed ? ArmedMode : Disarmed, by, ct);

    /// <summary>Null when the request names an unknown mode.</summary>
    public async Task<ArmedDto?> SetAsync(SetArmedRequest request, string by, CancellationToken ct)
    {
        var mode = request.Mode ?? (request.Armed ? ArmedMode : Disarmed);
        return IsKnown(mode) ? await SetModeAsync(mode, by, ct) : null;
    }

    public async Task<ArmedDto> SetModeAsync(string guard, string by, CancellationToken ct)
    {
        if (!IsKnown(guard))
            throw new ArgumentOutOfRangeException(nameof(guard), guard, "Unknown guard mode");
        var armed = guard != Disarmed;
        var now = time.GetUtcNow();
        var mode = await db.Modes.FirstAsync(m => m.Key == Mode.Armed, ct);
        mode.Value = armed ? "true" : "false";
        mode.UpdatedAt = now;
        mode.UpdatedBy = by;

        // Created on first use, so older databases need no migration.
        var presence = await db.Modes.FirstOrDefaultAsync(m => m.Key == Mode.Presence, ct);
        if (presence is null)
        {
            presence = new Mode { Key = Mode.Presence, Value = Mode.PresenceHome };
            db.Modes.Add(presence);
        }
        presence.Value = guard switch { Arriving => Mode.PresenceArriving, ArmedMode => Mode.PresenceAway, _ => Mode.PresenceHome };
        presence.UpdatedAt = now;
        presence.UpdatedBy = by;

        Audit.Record(db, now, by, guard switch { Arriving => "arm-arriving", ArmedMode => "arm", _ => "disarm" });
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
