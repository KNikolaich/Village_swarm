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

public sealed record ZoneDto(int Id, string Name, ZoneKind Kind, int DeviceCount, int Sort);

/// <summary>Sort null keeps the zone's place (a new zone goes after the others).</summary>
public sealed record SaveZoneRequest(string Name, ZoneKind Kind, int? Sort = null);

public sealed record SetDeviceSortRequest(int Sort);

public sealed record SetDeviceZoneRequest(int? ZoneId);

/// <summary>Shown null: back to the defaults of the hornet's kind.</summary>
public sealed record SetDeviceDashboardRequest(IReadOnlyList<string>? Shown);

public enum ZoneChange { Ok, NotFound, BadRequest, Duplicate }

/// <summary>Zones of the property ("Улица", "Гостиная"): created in the UI, hornets are assigned to them (spec 6.4 zones).</summary>
public sealed class ZoneService(HiveDbContext db, TimeProvider time)
{
    public const int MaxSort = 9999;

    private static bool SortOk(int? sort) => sort is null or >= 0 and <= MaxSort;

    public async Task<IReadOnlyList<ZoneDto>> ListAsync(CancellationToken ct) =>
        await db.Zones.AsNoTracking().OrderBy(z => z.Sort).ThenBy(z => z.Id)
            .Select(z => new ZoneDto(z.Id, z.Name, z.Kind, db.Devices.Count(d => d.ZoneId == z.Id), z.Sort))
            .ToListAsync(ct);

    public async Task<(ZoneChange Result, ZoneDto? Zone)> CreateAsync(SaveZoneRequest request, string by, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 64 || !Enum.IsDefined(request.Kind) || !SortOk(request.Sort))
            return (ZoneChange.BadRequest, null);
        if (await db.Zones.AnyAsync(z => z.Name == name, ct))
            return (ZoneChange.Duplicate, null);
        // Without a place of its own a new zone goes last, so adding one does not reorder the screen.
        var sort = request.Sort ?? (await db.Zones.MaxAsync(z => (int?)z.Sort, ct) ?? 0);
        var zone = new Zone { Name = name, Kind = request.Kind, Sort = sort };
        db.Zones.Add(zone);
        Audit.Record(db, time.GetUtcNow(), by, "zone_create", name);
        await db.SaveChangesAsync(ct);
        return (ZoneChange.Ok, new ZoneDto(zone.Id, zone.Name, zone.Kind, 0, zone.Sort));
    }

    public async Task<ZoneChange> UpdateAsync(int id, SaveZoneRequest request, string by, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 64 || !Enum.IsDefined(request.Kind) || !SortOk(request.Sort))
            return ZoneChange.BadRequest;
        var zone = await db.Zones.FirstOrDefaultAsync(z => z.Id == id, ct);
        if (zone is null)
            return ZoneChange.NotFound;
        if (await db.Zones.AnyAsync(z => z.Name == name && z.Id != id, ct))
            return ZoneChange.Duplicate;
        zone.Name = name;
        zone.Kind = request.Kind;
        if (request.Sort is { } place)
            zone.Sort = place;
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

    /// <summary>A hornet's place in the swarm list; only the screen uses it, the hornet is not told.</summary>
    public async Task<ZoneChange> SortDeviceAsync(string deviceId, int sort, string by, CancellationToken ct)
    {
        if (!SortOk(sort))
            return ZoneChange.BadRequest;
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return ZoneChange.NotFound;
        if (device.Sort == sort)
            return ZoneChange.Ok;
        device.Sort = sort;
        Audit.Record(db, time.GetUtcNow(), by, "device_sort", deviceId, new { sort });
        await db.SaveChangesAsync(ct);
        return ZoneChange.Ok;
    }

    public const int MaxDashboardItems = 32;

    /// <summary>A key is a metric name (temperature_2, battery_voltage) or a button (snapshot, light, relay).</summary>
    private static bool DashboardKeyOk(string key) =>
        key.Length is > 0 and <= 48 && key.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.');

    /// <summary>What the zone cards and the overview show of a hornet; only the screen uses it, the hornet is not told.</summary>
    public async Task<ZoneChange> SetDashboardAsync(string deviceId, IReadOnlyList<string>? shown, string by, CancellationToken ct)
    {
        var keys = shown?.Distinct().ToList();
        if (keys is not null && (keys.Count > MaxDashboardItems || !keys.All(DashboardKeyOk)))
            return ZoneChange.BadRequest;
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
            return ZoneChange.NotFound;
        device.Dashboard = keys;
        Audit.Record(db, time.GetUtcNow(), by, "device_dashboard", deviceId, new { shown = keys });
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
                    _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A name of 1..64 characters, a kind and a sort of 0..9999"] }),
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
                    _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A name of 1..64 characters, a kind and a sort of 0..9999"] }),
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

        app.MapPut("/api/devices/{deviceId}/sort", async Task<Results<NoContent, NotFound, ValidationProblem>> (
                string deviceId, SetDeviceSortRequest request, ClaimsPrincipal user, ZoneService z, CancellationToken ct) =>
                await z.SortDeviceAsync(deviceId, request.Sort, $"user:{user.Identity?.Name}", ct) switch
                {
                    ZoneChange.Ok => TypedResults.NoContent(),
                    ZoneChange.NotFound => TypedResults.NotFound(),
                    _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["sort"] = [$"0..{ZoneService.MaxSort}"] }),
                })
            .WithTags("devices")
            .RequireAuthorization(HiveRoles.AdminOnly);

        app.MapPut("/api/devices/{deviceId}/dashboard", async Task<Results<NoContent, NotFound, ValidationProblem>> (
                string deviceId, SetDeviceDashboardRequest request, ClaimsPrincipal user, ZoneService z, CancellationToken ct) =>
                await z.SetDashboardAsync(deviceId, request.Shown, $"user:{user.Identity?.Name}", ct) switch
                {
                    ZoneChange.Ok => TypedResults.NoContent(),
                    ZoneChange.NotFound => TypedResults.NotFound(),
                    _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["shown"] = [$"Up to {ZoneService.MaxDashboardItems} keys of a-z, 0-9, _ and ."] }),
                })
            .WithTags("devices")
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }
}
