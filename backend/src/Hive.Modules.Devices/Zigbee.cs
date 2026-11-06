using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Contracts.Mqtt;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Hive.Infrastructure.Mqtt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Devices;

/// <summary>A device paired with zigbee2mqtt; DeviceId is filled when it has been added to the hive.</summary>
public sealed record ZigbeeDeviceDto(
    string Ieee, string FriendlyName, string Type, string? Vendor, string? Model, string? Description,
    bool Supported, bool InterviewCompleted, string? DeviceId);

public sealed record ZigbeeDto(bool Enabled, string Bridge, DateTimeOffset? PermitJoinUntil, IReadOnlyList<ZigbeeDeviceDto> Devices);

public sealed record PermitJoinRequest(int Seconds);

public sealed record AddZigbeeDeviceRequest(string Ieee, string DeviceId, string Type, string Name, int? ZoneId = null);

/// <summary>Pairing Zigbee devices through zigbee2mqtt (docs/zigbee.md): allow joining, see what was found, add it as a hornet.</summary>
public sealed class ZigbeeService(HiveDbContext db, MqttGateway mqtt, ZigbeeCatalog catalog, IOptions<ZigbeeOptions> options, TimeProvider time)
{
    public const int MaxPermitSeconds = 254;

    public static string MacOf(string ieee) => (ieee.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ieee[2..] : ieee).ToUpperInvariant();

    public async Task<ZigbeeDto> GetAsync(CancellationToken ct)
    {
        var added = await db.Devices.AsNoTracking().Where(d => d.Driver == Drivers.Zigbee && d.Mac != null)
            .Select(d => new { d.Mac, d.DeviceId }).ToDictionaryAsync(d => d.Mac!, d => d.DeviceId, ct);
        var devices = catalog.Devices.Select(d => new ZigbeeDeviceDto(
            d.Ieee, d.FriendlyName, d.Type, d.Vendor, d.Model, d.Description, d.Supported, d.InterviewCompleted,
            added.GetValueOrDefault(MacOf(d.Ieee)))).ToList();
        return new ZigbeeDto(options.Value.Enabled, catalog.Bridge, catalog.PermitJoinUntil(time), devices);
    }

    /// <summary>Opens (or, with 0, closes) pairing mode. False when Zigbee is off or the broker is down.</summary>
    public async Task<bool> PermitJoinAsync(int seconds, string by, CancellationToken ct)
    {
        if (!options.Value.Enabled || !mqtt.IsConnected)
            return false;
        seconds = Math.Clamp(seconds, 0, MaxPermitSeconds);
        await mqtt.PublishAsync(ZigbeeTopics.PermitJoin(options.Value.BaseTopic), new JsonObject { ["time"] = seconds }.ToJsonString(), retain: false, ct);
        catalog.SetPermitJoin(seconds == 0 ? null : time.GetUtcNow().AddSeconds(seconds));
        Audit.Record(db, time.GetUtcNow(), by, "zigbee_permit_join", null, new { seconds });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Adds a paired device as a hornet: zigbee2mqtt renames it to the device_id (its friendly name is how the hive finds
    /// the device's reports), and the hive creates the device. Null error: done.
    /// </summary>
    public async Task<string?> AddAsync(AddZigbeeDeviceRequest r, string by, CancellationToken ct)
    {
        if (!options.Value.Enabled)
            return "Zigbee не включён (Zigbee__Enabled)";
        if (!Topics.IsValidDeviceId(r.DeviceId))
            return "device_id: латиница в нижнем регистре, цифры и дефисы, например door-gate1";
        if (!await db.DeviceTypes.AnyAsync(t => t.Code == r.Type, ct))
            return $"unknown type {r.Type}";
        if (string.IsNullOrWhiteSpace(r.Name))
            return "name is required";
        var found = catalog.Devices.FirstOrDefault(d => MacOf(d.Ieee) == MacOf(r.Ieee));
        if (found is null)
            return "Такого устройства нет среди спаренных с zigbee2mqtt";
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == r.DeviceId, ct);
        if (device is not null && device.Driver != Drivers.Zigbee && device.Status != DeviceStatus.Disabled)
            return $"{r.DeviceId} уже занят другим шершнем; выберите другой id";
        if (!mqtt.IsConnected)
            return "Нет связи с брокером";

        if (found.FriendlyName != r.DeviceId)
            await mqtt.PublishAsync(ZigbeeTopics.Rename(options.Value.BaseTopic),
                new JsonObject { ["from"] = found.FriendlyName, ["to"] = r.DeviceId }.ToJsonString(), retain: false, ct);

        var now = time.GetUtcNow();
        if (device is null)
        {
            device = new Device { DeviceId = r.DeviceId, TypeCode = r.Type, Name = r.Name.Trim(), CreatedAt = now };
            db.Devices.Add(device);
        }
        device.TypeCode = r.Type;
        device.Name = r.Name.Trim();
        device.ZoneId = r.ZoneId ?? device.ZoneId;
        device.Driver = Drivers.Zigbee;
        device.Hw = found.Model ?? "zigbee";
        device.Mac = MacOf(found.Ieee);
        device.Status = DeviceStatus.Pending;
        device.MqttUser = null;
        device.UploadTokenHash = null;
        device.Info = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            caps = new[] { "zigbee" },
            metrics = Array.Empty<string>(),
            commands = r.Type == "relay" ? new[] { "relay" } : Array.Empty<string>(),
        }));
        Audit.Record(db, now, by, "device_add_zigbee", r.DeviceId, new { ieee = device.Mac, found.Model });
        await db.SaveChangesAsync(ct);
        return null;
    }
}

public static class ZigbeeEndpoints
{
    public static IEndpointRouteBuilder MapZigbeeEndpoints(this IEndpointRouteBuilder app)
    {
        var zigbee = app.MapGroup("/api/zigbee").WithTags("zigbee");
        zigbee.MapGet("", async (ZigbeeService service, CancellationToken ct) => await service.GetAsync(ct))
            .RequireAuthorization(HiveRoles.AdminOnly);
        zigbee.MapPost("/permit-join", async Task<Results<NoContent, ProblemHttpResult>> (
                PermitJoinRequest request, ClaimsPrincipal user, ZigbeeService service, CancellationToken ct) =>
                await service.PermitJoinAsync(request.Seconds, $"user:{user.Identity?.Name}", ct)
                    ? TypedResults.NoContent()
                    : TypedResults.Problem("Zigbee выключен или нет связи с брокером", statusCode: StatusCodes.Status503ServiceUnavailable))
            .RequireAuthorization(HiveRoles.AdminOnly);
        zigbee.MapPost("/devices", async Task<Results<NoContent, ValidationProblem>> (
                AddZigbeeDeviceRequest request, ClaimsPrincipal user, ZigbeeService service, CancellationToken ct) =>
            {
                var error = await service.AddAsync(request, $"user:{user.Identity?.Name}", ct);
                return error is null
                    ? TypedResults.NoContent()
                    : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [error] });
            })
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }
}
