using System.Security.Claims;
using System.Text.Json;
using Hive.Domain;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Modules.Devices;

public sealed record DeviceDto(
    string DeviceId, string Name, string Type, string Status, int? ZoneId, string? Zone,
    string? Hw, string? Fw, string? Mac, string? Ip, int? Rssi, int? Boot, DateTimeOffset? LastSeenAt,
    JsonElement? Info, JsonElement? State, int ConfigRev, int? ConfigAppliedRev, int? SleepS, string SleepMode);

/// <summary>Device registry reads (spec 6.5). Editing, commands and provisioning come in later steps.</summary>
/// <summary>Pins of a hornet: what each function uses, from the board default or the config, and conflicts.</summary>
public sealed record PinsView(IReadOnlyList<BoardPins.PinRow> Pins, IReadOnlyList<string> Conflicts);

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
        d.Info?.RootElement.Clone(), d.State?.RootElement.Clone(), d.ConfigRev, d.ConfigAppliedRev, DeviceConfigService.SleepS(d), DeviceConfigService.SleepMode(d));
}

public static class DevicesModule
{
    public const string Name = "devices";

    public static IServiceCollection AddDevicesModule(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.Configure<ProvisioningOptions>(configuration.GetSection(ProvisioningOptions.Section));
        services.AddScoped<DeviceQueries>();
        services.AddScoped<CommandService>();
        services.AddScoped<ArmService>();
        services.AddScoped<ProvisioningService>();
        services.AddHostedService<DynamicSecurityBootstrap>();
        services.AddHostedService<CommandTimeoutJob>();
        services.AddScoped<DeviceConfigService>();
        services.AddSingleton<SleepingDeviceWatch>();
        services.AddHostedService(sp => sp.GetRequiredService<SleepingDeviceWatch>());
        return services;
    }

    public static IEndpointRouteBuilder MapDevicesEndpoints(this IEndpointRouteBuilder app)
    {
        var devices = app.MapGroup("/api/devices").WithTags("devices");
        devices.MapGet("", (int? zone, string? type, string? status, DeviceQueries q, CancellationToken ct) =>
            q.ListAsync(zone, type, status, ct));
        devices.MapGet("/{deviceId}", async Task<Results<Ok<DeviceDto>, NotFound>> (string deviceId, DeviceQueries q, CancellationToken ct) =>
            await q.GetAsync(deviceId, ct) is { } d ? TypedResults.Ok(d) : TypedResults.NotFound());

        devices.MapPost("/{deviceId}/commands", async Task<Results<Accepted<CommandDto>, NotFound, ValidationProblem, ProblemHttpResult>> (
                string deviceId, SendCommandRequest request, ClaimsPrincipal user, CommandService commands, CancellationToken ct) =>
            {
                var (status, command) = await commands.SendAsync(deviceId, request.Name, request.Payload, $"user:{user.Identity?.Name}", request.TtlS, ct);
                return status switch
                {
                    SendStatus.Sent => TypedResults.Accepted($"/api/devices/{deviceId}/commands/{command!.Cid}", CommandDto.From(command)),
                    SendStatus.UnknownDevice => TypedResults.NotFound(),
                    SendStatus.BadRequest => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Invalid command name"] }),
                    _ => TypedResults.Problem("MQTT broker is not connected", statusCode: StatusCodes.Status503ServiceUnavailable),
                };
            })
            .RequireAuthorization(HiveRoles.CanControl);
        devices.MapGet("/{deviceId}/commands/{cid}", async Task<Results<Ok<CommandDto>, NotFound>> (string deviceId, string cid, CommandService commands, CancellationToken ct) =>
            await commands.GetAsync(deviceId, cid, ct) is { } c ? TypedResults.Ok(CommandDto.From(c)) : TypedResults.NotFound());

        devices.MapPatch("/{deviceId}/config", async Task<Results<Ok<DeviceDto>, NotFound, ValidationProblem>> (
                string deviceId, UpdateDeviceConfigRequest request, ClaimsPrincipal user, DeviceConfigService configs, DeviceQueries q, CancellationToken ct) =>
            {
                if (DeviceConfigService.Validate(request) is { } error)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [error] });
                return await configs.UpdateAsync(deviceId, request, $"user:{user.Identity?.Name}", ct) == ConfigUpdateStatus.NotFound
                    ? TypedResults.NotFound()
                    : TypedResults.Ok((await q.GetAsync(deviceId, ct))!);
            })
            .RequireAuthorization(HiveRoles.CanControl);

        devices.MapGet("/{deviceId}/pins", async Task<Results<Ok<PinsView>, NotFound>> (string deviceId, DeviceConfigService configs, CancellationToken ct) =>
            await configs.GetPinsAsync(deviceId, ct) is { Status: ConfigUpdateStatus.Updated } r
                ? TypedResults.Ok(new PinsView(r.Rows, r.Conflicts))
                : TypedResults.NotFound());

        devices.MapPut("/{deviceId}/pins", async Task<Results<Ok<PinsView>, NotFound, ValidationProblem>> (
                string deviceId, DeviceConfigService.PinsRequest request, ClaimsPrincipal user, DeviceConfigService configs, CancellationToken ct) =>
            {
                var r = await configs.SetPinsAsync(deviceId, request, $"user:{user.Identity?.Name}", ct);
                return r.Status switch
                {
                    ConfigUpdateStatus.NotFound => TypedResults.NotFound(),
                    ConfigUpdateStatus.BadRequest => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["pins"] = [r.Error ?? "bad pins"] }),
                    _ => TypedResults.Ok(new PinsView(r.Rows, r.Conflicts)),
                };
            })
            .RequireAuthorization(HiveRoles.CanControl);

        app.MapProvisioningEndpoints();

        var modes = app.MapGroup("/api/modes").WithTags("modes");
        modes.MapGet("/armed", (ArmService arm, CancellationToken ct) => arm.GetAsync(ct));
        modes.MapPut("/armed", (SetArmedRequest request, ClaimsPrincipal user, ArmService arm, CancellationToken ct) =>
                arm.SetAsync(request.Armed, $"user:{user.Identity?.Name}", ct))
            .RequireAuthorization(HiveRoles.CanControl);
        return app;
    }
}
