using System.Security.Claims;
using Hive.Domain;
using Hive.Infrastructure;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Modules.Devices;

public sealed record ZoneDto(int Id, string Name, ZoneKind Kind, int DeviceCount);

public sealed record SaveZoneRequest(string Name, ZoneKind Kind);

public sealed record SetDeviceZoneRequest(int? ZoneId);

public enum ZoneChange { Ok, NotFound, BadRequest, Duplicate }

/// <summary>Zones of the property ("Улица", "Гостиная"): created in the UI, hornets are assigned to them (spec 6.4 zones).</summary>
public sealed class ZoneService(HiveDbContext db, TimeProvider time)
{
    public async Task<IReadOnlyList<ZoneDto>> ListAsync(CancellationToken ct) =>
        await db.Zones.AsNoTracking().OrderBy(z => z.Id)
            .Select(z => new ZoneDto(z.Id, z.Name, z.Kind, db.Devices.Count(d => d.ZoneId == z.Id)))
            .ToListAsync(ct);

    public async Task<(ZoneChange Result, ZoneDto? Zone)> CreateAsync(SaveZoneRequest request, string by, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 64 || !Enum.IsDefined(request.Kind))
            return (ZoneChange.BadRequest, null);
        if (await db.Zones.AnyAsync(z => z.Name == name, ct))
            return (ZoneChange.Duplicate, null);
        var zone = new Zone { Name = name, Kind = request.Kind };
        db.Zones.Add(zone);
        Audit.Record(db, time.GetUtcNow(), by, "zone_create", name);
        await db.SaveChangesAsync(ct);
        return (ZoneChange.Ok, new ZoneDto(zone.Id, zone.Name, zone.Kind, 0));
    }

    public async Task<ZoneChange> UpdateAsync(int id, SaveZoneRequest request, string by, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 64 || !Enum.IsDefined(request.Kind))
            return ZoneChange.BadRequest;
        var zone = await db.Zones.FirstOrDefaultAsync(z => z.Id == id, ct);
        if (zone is null)
            return ZoneChange.NotFound;
        if (await db.Zones.AnyAsync(z => z.Name == name && z.Id != id, ct))
            return ZoneChange.Duplicate;
        zone.Name = name;
        zone.Kind = request.Kind;
        Audit.Record(db, time.GetUtcNow(), by, "zone_update", name);
        await db.SaveChangesAsync(ct);
        return ZoneChange.Ok;
    }

    /// <summary>Deleting a zone leaves its hornets without a zone (the foreign key sets null).</summary>
    public async Task<ZoneChange> DeleteAsync(int id, string by, CancellationToken ct)
    {
        var zone = await db.Zones.FirstOrDefaultAsync(z => z.Id == id, ct);
        if (zone is null)
            return ZoneChange.NotFound;
        db.Zones.Remove(zone);
        Audit.Record(db, time.GetUtcNow(), by, "zone_delete", zone.Name);
        await db.SaveChangesAsync(ct);
        return ZoneChange.Ok;
    }

    public async Task<ZoneChange> AssignAsync(string deviceId, int? zoneId, string by, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return ZoneChange.NotFound;
        if (zoneId is { } id && !await db.Zones.AnyAsync(z => z.Id == id, ct))
            return ZoneChange.BadRequest;
        device.ZoneId = zoneId;
        Audit.Record(db, time.GetUtcNow(), by, "device_zone", deviceId, new { zoneId });
        await db.SaveChangesAsync(ct);
        return ZoneChange.Ok;
    }
}

/// <summary>The first start has the zones Kirill asked for, so the climate screen is not empty; after that they are his.</summary>
public sealed class ZoneBootstrap(IServiceScopeFactory scopes) : IHostedService
{
    private static readonly (string Name, ZoneKind Kind)[] Defaults =
    [
        ("Улица", ZoneKind.Garden), ("Гостиная", ZoneKind.House), ("Спальня", ZoneKind.House), ("Детская", ZoneKind.House),
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The build runs the app to write the OpenAPI document (frontend/src/api); it must not need a database.
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
            return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        if (await db.Zones.AnyAsync(cancellationToken))
            return;
        db.Zones.AddRange(Defaults.Select(d => new Zone { Name = d.Name, Kind = d.Kind }));
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class ZoneEndpoints
{
    public static IEndpointRouteBuilder MapZoneEndpoints(this IEndpointRouteBuilder app)
    {
        var zones = app.MapGroup("/api/zones").WithTags("zones");
        zones.MapGet("", (ZoneService z, CancellationToken ct) => z.ListAsync(ct));

        zones.MapPost("", async Task<Results<Created<ZoneDto>, ValidationProblem, Conflict>> (
                SaveZoneRequest request, ClaimsPrincipal user, ZoneService z, CancellationToken ct) =>
            {
                var (result, zone) = await z.CreateAsync(request, $"user:{user.Identity?.Name}", ct);
                return result switch
                {
                    ZoneChange.Ok => TypedResults.Created($"/api/zones/{zone!.Id}", zone),
                    ZoneChange.Duplicate => TypedResults.Conflict(),
                    _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A name of 1..64 characters and a kind"] }),
                };
            })
            .RequireAuthorization(HiveRoles.AdminOnly);

        zones.MapPut("/{id:int}", async Task<Results<NoContent, NotFound, ValidationProblem, Conflict>> (
                int id, SaveZoneRequest request, ClaimsPrincipal user, ZoneService z, CancellationToken ct) =>
                await z.UpdateAsync(id, request, $"user:{user.Identity?.Name}", ct) switch
                {
                    ZoneChange.Ok => TypedResults.NoContent(),
                    ZoneChange.NotFound => TypedResults.NotFound(),
                    ZoneChange.Duplicate => TypedResults.Conflict(),
                    _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A name of 1..64 characters and a kind"] }),
                })
            .RequireAuthorization(HiveRoles.AdminOnly);

        zones.MapDelete("/{id:int}", async Task<Results<NoContent, NotFound>> (int id, ClaimsPrincipal user, ZoneService z, CancellationToken ct) =>
                await z.DeleteAsync(id, $"user:{user.Identity?.Name}", ct) == ZoneChange.Ok ? TypedResults.NoContent() : TypedResults.NotFound())
            .RequireAuthorization(HiveRoles.AdminOnly);

        app.MapPut("/api/devices/{deviceId}/zone", async Task<Results<NoContent, NotFound, ValidationProblem>> (
                string deviceId, SetDeviceZoneRequest request, ClaimsPrincipal user, ZoneService z, CancellationToken ct) =>
                await z.AssignAsync(deviceId, request.ZoneId, $"user:{user.Identity?.Name}", ct) switch
                {
                    ZoneChange.Ok => TypedResults.NoContent(),
                    ZoneChange.NotFound => TypedResults.NotFound(),
                    _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["zoneId"] = ["No such zone"] }),
                })
            .WithTags("devices")
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }
}
