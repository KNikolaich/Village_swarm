using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Modules.Devices;

public sealed record DeviceDto(
    string DeviceId, string Name, string Type, string Status, int? ZoneId, string? Zone,
    string? Hw, string? Fw, string? Mac, string? Ip, int? Rssi, int? Boot, DateTimeOffset? LastSeenAt,
    JsonElement? Info, JsonElement? State, int ConfigRev, int? ConfigAppliedRev);

/// <summary>Device registry reads (spec 6.5). Editing, commands and provisioning come in later steps.</summary>
public sealed class DeviceQueries(HiveDbContext db)
{
    public async Task<IReadOnlyList<DeviceDto>> ListAsync(int? zone, string? type, string? status, CancellationToken ct)
    {
        var query = db.Devices.AsNoTracking().Include(d => d.Zone).AsQueryable();
        if (zone is { } z)
            query = query.Where(d => d.ZoneId == z);
        if (!string.IsNullOrEmpty(type))
            query = query.Where(d => d.TypeCode == type);
        if (Enum.TryParse<DeviceStatus>(status, ignoreCase: true, out var s))
            query = query.Where(d => d.Status == s);
        return (await query.OrderBy(d => d.DeviceId).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<DeviceDto?> GetAsync(string deviceId, CancellationToken ct) =>
        await db.Devices.AsNoTracking().Include(d => d.Zone).FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct) is { } d
            ? ToDto(d)
            : null;

    private static DeviceDto ToDto(Device d) => new(
        d.DeviceId, d.Name, d.TypeCode, d.Status.ToString().ToLowerInvariant(), d.ZoneId, d.Zone?.Name,
        d.Hw, d.FwVersion, d.Mac, d.Ip, d.Rssi, d.Boot, d.LastSeenAt,
        d.Info?.RootElement.Clone(), d.State?.RootElement.Clone(), d.ConfigRev, d.ConfigAppliedRev);
}

public static class DevicesModule
{
    public const string Name = "devices";

    public static IServiceCollection AddDevicesModule(this IServiceCollection services) =>
        services.AddScoped<DeviceQueries>();

    public static IEndpointRouteBuilder MapDevicesEndpoints(this IEndpointRouteBuilder app)
    {
        var devices = app.MapGroup("/api/devices");
        devices.MapGet("", (int? zone, string? type, string? status, DeviceQueries q, CancellationToken ct) =>
            q.ListAsync(zone, type, status, ct));
        devices.MapGet("/{deviceId}", async (string deviceId, DeviceQueries q, CancellationToken ct) =>
            await q.GetAsync(deviceId, ct) is { } d ? Results.Ok(d) : Results.NotFound());
        return app;
    }
}
