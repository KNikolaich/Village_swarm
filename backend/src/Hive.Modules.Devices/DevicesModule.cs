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
using Hive.Infrastructure.Mqtt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Modules.Devices;

public sealed record DeviceDto(
    string DeviceId, string Name, string Type, string Status, int? ZoneId, string? Zone,
    string? Hw, string? Fw, string? Mac, string? Ip, int? Rssi, int? Boot, DateTimeOffset? LastSeenAt,
    JsonElement? Info, JsonElement? State, int ConfigRev, int? ConfigAppliedRev, int? SleepS, string SleepMode, string Driver,
    IReadOnlyList<string>? Features = null, IReadOnlyList<string>? SupportedFeatures = null, HeaterLimitsDto? Heater = null, int Sort = 0,
    string? Thermometer = null, IReadOnlyList<string>? Dashboard = null);

/// <summary>The limits a heater keeps by itself (config failsafe.heater, or the firmware defaults).</summary>
public sealed record HeaterLimitsDto(double MinC, double MaxC, int MaxOnS);

/// <summary>Device registry reads (spec 6.5). Editing, commands and provisioning come in later steps.</summary>
/// <summary>Pins of a hornet: what each function uses, from the board default or the config, and conflicts. VbatRatio is the battery divider ratio set in the config (null: 1.0).</summary>
/// Thermometer is the sensor its feature "thermometer" reads (ds18b20, dht11, dht22; null for a role without one), ThermometerByHand whether the config sets it.
public sealed record PinsView(IReadOnlyList<BoardPins.PinRow> Pins, IReadOnlyList<string> Conflicts, double? VbatRatio, string? Thermometer = null, bool ThermometerByHand = false);

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
        d.Info?.RootElement.Clone(), d.State?.RootElement.Clone(), d.ConfigRev, d.ConfigAppliedRev, DeviceConfigService.SleepS(d), DeviceConfigService.SleepMode(d), d.Driver,
        Features(d), d.Driver == Drivers.Hornet ? DeviceFeatures.SupportedBy(d.TypeCode) : null, HeaterLimits(d), d.Sort,
        Thermometer(d), d.Dashboard);

    /// <summary>Features of a native hornet (Tasmota devices keep their own settings).</summary>
    private static IReadOnlyList<string>? Features(Device d) =>
        d.Driver == Drivers.Hornet ? DeviceFeatures.Effective(d.TypeCode, d.Config?.RootElement) : null;

    /// <summary>The sensor a hornet with the feature "thermometer" reads (a DHT also gives humidity), else null.</summary>
    private static string? Thermometer(Device d) =>
        Features(d)?.Contains(DeviceFeatures.Thermometer) == true
            ? BoardPins.ThermometerSensor(d.Hw, d.Config?.RootElement.TryGetProperty("hw", out var hw) == true ? hw : null)
            : null;

    private static HeaterLimitsDto? HeaterLimits(Device d)
    {
        if (Features(d)?.Contains(DeviceFeatures.Heater) != true)
            return null;
        var (minC, maxC, maxOnS) = DeviceConfigService.Heater(d) ?? (DeviceFeatures.DefaultMinC, DeviceFeatures.DefaultMaxC, DeviceFeatures.DefaultMaxOnS);
        return new HeaterLimitsDto(minC, maxC, maxOnS);
    }
}

public static class DevicesModule
{
    public const string Name = "devices";

    public static IServiceCollection AddDevicesModule(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.Configure<ProvisioningOptions>(configuration.GetSection(ProvisioningOptions.Section));
        services.AddScoped<DeviceQueries>();
        services.TryAddSingleton<TasmotaPending>();
        services.AddScoped<CommandService>();
        services.AddScoped<ArmService>();
        services.AddScoped<ZoneService>();
        services.AddScoped<DashboardLayoutService>();
        services.AddScoped<IrButtonService>();
        services.AddScoped<ZigbeeService>();
        services.AddHostedService<ZoneBootstrap>();
        services.AddScoped<ProvisioningService>();
        services.AddHostedService<DynamicSecurityBootstrap>();
        services.AddHostedService<CommandTimeoutJob>();
        services.AddScoped<DeviceConfigService>();
        services.AddScoped<OtaService>();
        services.AddScoped<OtaPendingStore>();
        services.AddSingleton<OtaWakeDelivery>();
        services.AddSingleton<IMqttMessageHandler>(sp => sp.GetRequiredService<OtaWakeDelivery>());
        services.AddSingleton<OtaRolloutRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<OtaRolloutRunner>());
        services.AddSingleton<SleepingDeviceWatch>();
        services.AddHostedService(sp => sp.GetRequiredService<SleepingDeviceWatch>());
        return services;
    }

    public static IEndpointRouteBuilder MapDevicesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapZigbeeEndpoints();
        var devices = app.MapGroup("/api/devices").WithTags("devices");
        devices.MapGet("", (int? zone, string? type, string? status, DeviceQueries q, CancellationToken ct) =>
            q.ListAsync(zone, type, status, ct));
        devices.MapGet("/{deviceId}", async Task<Results<Ok<DeviceDto>, NotFound>> (string deviceId, DeviceQueries q, CancellationToken ct) =>
            await q.GetAsync(deviceId, ct) is { } d ? TypedResults.Ok(d) : TypedResults.NotFound());

        devices.MapPost("/{deviceId}/commands", async Task<Results<Accepted<CommandDto>, NotFound, ValidationProblem, ProblemHttpResult>> (
                string deviceId, SendCommandRequest request, ClaimsPrincipal user, CommandService commands, CancellationToken ct) =>
            {
                // The Tasmota console can reconfigure a device: administrators only.
                if (request.Name == "tasmota" && !user.IsInRole(HiveRoles.Admin))
                    return TypedResults.Problem("Консоль Tasmota только для администратора", statusCode: StatusCodes.Status403Forbidden);
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
        devices.MapGet("/{deviceId}/commands", async (string deviceId, int? limit, CommandService commands, CancellationToken ct) =>
            (await commands.ListAsync(deviceId, limit, ct)).Select(CommandDto.From).ToList())
            .RequireAuthorization(HiveRoles.CanControl);
        devices.MapGet("/{deviceId}/commands/{cid}", async Task<Results<Ok<CommandDto>, NotFound>> (string deviceId, string cid, CommandService commands, CancellationToken ct) =>
            await commands.GetAsync(deviceId, cid, ct) is { } c ? TypedResults.Ok(CommandDto.From(c)) : TypedResults.NotFound());

        // Learned buttons of a Tasmota IR device (docs/tasmota.md): read by everyone who can see it, edited by those who control.
        devices.MapGet("/{deviceId}/ir-buttons", async Task<Results<Ok<IrButtonsDto>, NotFound>> (string deviceId, IrButtonService ir, CancellationToken ct) =>
            await ir.GetAsync(deviceId, ct) is { } b ? TypedResults.Ok(b) : TypedResults.NotFound());
        devices.MapPut("/{deviceId}/ir-buttons", async Task<Results<Ok<IrButtonsDto>, NotFound, ValidationProblem>> (
                string deviceId, IrButtonsDto request, ClaimsPrincipal user, IrButtonService ir, CancellationToken ct) =>
            {
                if (IrButtonService.Validate(request.Buttons) is { } error)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["buttons"] = [error] });
                return await ir.SetAsync(deviceId, request.Buttons, $"user:{user.Identity?.Name}", ct)
                    ? TypedResults.Ok((await ir.GetAsync(deviceId, ct))!)
                    : TypedResults.NotFound();
            })
            .RequireAuthorization(HiveRoles.CanControl);

        // The thermometer a Bluetooth bridge is paired with (docs/ble-thermometer.md).
        devices.MapGet("/{deviceId}/ble", async Task<Results<Ok<DeviceConfigService.BleRequest>, NotFound>> (string deviceId, DeviceConfigService configs, CancellationToken ct) =>
            await configs.GetBleAsync(deviceId, ct) is { } b ? TypedResults.Ok(b) : TypedResults.NotFound());
        devices.MapPut("/{deviceId}/ble", async Task<Results<Ok<DeviceConfigService.BleRequest>, NotFound, ValidationProblem>> (
                string deviceId, DeviceConfigService.BleRequest request, ClaimsPrincipal user, DeviceConfigService configs, CancellationToken ct) =>
            {
                if (DeviceConfigService.ValidateBle(request) is { } error)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["mac"] = [error] });
                return await configs.SetBleAsync(deviceId, request, $"user:{user.Identity?.Name}", ct) == ConfigUpdateStatus.NotFound
                    ? TypedResults.NotFound()
                    : TypedResults.Ok((await configs.GetBleAsync(deviceId, ct))!);
            })
            .RequireAuthorization(HiveRoles.CanControl);

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

        devices.MapPut("/{deviceId}/name", async Task<Results<Ok<DeviceDto>, NotFound, ValidationProblem>> (
                string deviceId, RenameDeviceRequest request, ClaimsPrincipal user, DeviceConfigService configs, DeviceQueries q, CancellationToken ct) =>
            {
                return await configs.RenameAsync(deviceId, request.Name, $"user:{user.Identity?.Name}", ct) switch
                {
                    ConfigUpdateStatus.NotFound => TypedResults.NotFound(),
                    ConfigUpdateStatus.BadRequest => TypedResults.ValidationProblem(new Dictionary<string, string[]>
                        { ["name"] = [$"name: от 1 до {DeviceConfigService.MaxNameLength} символов"] }),
                    _ => TypedResults.Ok((await q.GetAsync(deviceId, ct))!),
                };
            })
            .RequireAuthorization(HiveRoles.AdminOnly);

        devices.MapGet("/{deviceId}/pins", async Task<Results<Ok<PinsView>, NotFound>> (string deviceId, DeviceConfigService configs, CancellationToken ct) =>
            await configs.GetPinsAsync(deviceId, ct) is { Status: ConfigUpdateStatus.Updated } r
                ? TypedResults.Ok(new PinsView(r.Rows, r.Conflicts, r.VbatRatio, r.Thermometer, r.ThermometerByHand))
                : TypedResults.NotFound());

        devices.MapPut("/{deviceId}/pins", async Task<Results<Ok<PinsView>, NotFound, ValidationProblem>> (
                string deviceId, DeviceConfigService.PinsRequest request, ClaimsPrincipal user, DeviceConfigService configs, CancellationToken ct) =>
            {
                var r = await configs.SetPinsAsync(deviceId, request, $"user:{user.Identity?.Name}", ct);
                return r.Status switch
                {
                    ConfigUpdateStatus.NotFound => TypedResults.NotFound(),
                    ConfigUpdateStatus.BadRequest => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["pins"] = [r.Error ?? "bad pins"] }),
                    _ => TypedResults.Ok(new PinsView(r.Rows, r.Conflicts, r.VbatRatio, r.Thermometer, r.ThermometerByHand)),
                };
            })
            .RequireAuthorization(HiveRoles.CanControl);

        devices.MapPut("/{deviceId}/features", async Task<Results<Ok<DeviceDto>, NotFound, ValidationProblem>> (
                string deviceId, DeviceConfigService.FeaturesRequest request, ClaimsPrincipal user, DeviceConfigService configs, DeviceQueries q, CancellationToken ct) =>
            {
                var r = await configs.SetFeaturesAsync(deviceId, request, $"user:{user.Identity?.Name}", ct);
                return r.Status switch
                {
                    ConfigUpdateStatus.NotFound => TypedResults.NotFound(),
                    ConfigUpdateStatus.BadRequest => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["features"] = [r.Error ?? "bad features"] }),
                    _ => TypedResults.Ok((await q.GetAsync(deviceId, ct))!),
                };
            })
            .RequireAuthorization(HiveRoles.AdminOnly);

        app.MapProvisioningEndpoints();
        app.MapOtaEndpoints();
        app.MapOtaPendingEndpoints();
        app.MapOtaRolloutEndpoints();

        var modes = app.MapGroup("/api/modes").WithTags("modes");
        modes.MapGet("/armed", (ArmService arm, CancellationToken ct) => arm.GetAsync(ct));
        modes.MapPut("/armed", async Task<Results<Ok<ArmedDto>, BadRequest>> (SetArmedRequest request, ClaimsPrincipal user, ArmService arm, CancellationToken ct) =>
                await arm.SetAsync(request, $"user:{user.Identity?.Name}", ct) is { } state ? (Results<Ok<ArmedDto>, BadRequest>)TypedResults.Ok(state) : TypedResults.BadRequest())
            .RequireAuthorization(HiveRoles.CanControl);
        return app;
    }
}
