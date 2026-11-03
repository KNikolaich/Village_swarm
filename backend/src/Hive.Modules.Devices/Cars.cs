using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Hive.Contracts.Messages;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hive.Modules.Devices;

/// <summary>Wheel speeds, -100..100 each: positive forward. {0, 0} stops.</summary>
public sealed record DriveRequest(int Left, int Right);

public enum DriveResult
{
    Ok,
    NotFound,
    BadRequest,
    BrokerDown,
}

/// <summary>
/// Drives a car (contracts/mqtt-topics.md "Car"): the request becomes a <see cref="DriveMessage"/> on vs/v1/hive/drive/{id},
/// QoS 0, no command row and no ack (the controls repeat it every 200 ms; the car stops 500 ms after the last one).
/// A move that starts after a stop is audited, not every repeat.
/// </summary>
public sealed class CarDrive(IServiceScopeFactory scopes, MqttGateway mqtt, TimeProvider time, ILogger<CarDrive> logger)
{
    /// <summary>Whether a device is a car is asked again after this long (a car can be removed or disabled).</summary>
    private static readonly TimeSpan KnownFor = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (bool Car, DateTimeOffset At)> _known = new();
    private readonly ConcurrentDictionary<string, bool> _moving = new();

    public static string? Invalid(DriveRequest r) =>
        r.Left is < -100 or > 100 || r.Right is < -100 or > 100 ? "left and right are speeds from -100 to 100" : null;

    public async Task<DriveResult> DriveAsync(string deviceId, DriveRequest r, string by, CancellationToken ct)
    {
        if (Invalid(r) is not null)
            return DriveResult.BadRequest;
        if (!await IsCarAsync(deviceId, ct))
            return DriveResult.NotFound;
        if (!mqtt.IsConnected)
            return DriveResult.BrokerDown;
        var now = time.GetUtcNow();
        var message = new DriveMessage { Ts = now.ToUnixTimeMilliseconds(), Left = r.Left, Right = r.Right };
        await mqtt.PublishAsync(Topics.Drive(deviceId), ContractJson.Serialize(message), retain: false, ct);

        var moving = r.Left != 0 || r.Right != 0;
        var was = _moving.TryGetValue(deviceId, out var before) && before;
        _moving[deviceId] = moving;
        if (moving && !was)
        {
            logger.LogInformation("Car {Device} driven by {By}", deviceId, by);
            await AuditAsync(by, deviceId, new { left = r.Left, right = r.Right });
        }
        return DriveResult.Ok;
    }

    private async Task<bool> IsCarAsync(string deviceId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_known.TryGetValue(deviceId, out var known) && now - known.At < KnownFor)
            return known.Car;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
        var car = await db.Devices.AsNoTracking()
            .AnyAsync(d => d.DeviceId == deviceId && d.TypeCode == Cars.Type && d.Status != DeviceStatus.Disabled, ct);
        _known[deviceId] = (car, now);
        return car;
    }

    private async Task AuditAsync(string by, string deviceId, object details)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HiveDbContext>();
            Audit.Record(db, time.GetUtcNow(), by, "car.drive", deviceId, details);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Drive audit of {Device} not saved", deviceId);
        }
    }
}

/// <summary>
/// The car's wiring and limits (config hw.motor_pins, hw.motor_pwm, car.*). MotorPins: IN1, IN2 (left), IN3, IN4 (right); null
/// keeps the firmware's 14, 15, 13, 12.
/// </summary>
public sealed record CarSetupDto(
    IReadOnlyList<int>? MotorPins = null, bool MotorPwm = true, bool InvertLeft = false, bool InvertRight = false, bool SwapSides = false,
    int MaxSpeed = 100, int MaxRunS = 60, int LightPin = 4, int MinSpeed = 40);

/// <summary>Reads and sets a car's wiring and limits in its config (published like any config change).</summary>
public sealed class CarSetup(HiveDbContext db, MqttGateway mqtt, TimeProvider time)
{
    public static readonly int[] DefaultMotorPins = [14, 15, 13, 12];

    /// <summary>Free pins of the ESP32-CAM: the camera, PSRAM (16, 17) and the serial console (1, 3) take the rest.</summary>
    public static readonly int[] FreePins = [2, 4, 12, 13, 14, 15];

    public static string? Validate(CarSetupDto r)
    {
        var pins = r.MotorPins ?? DefaultMotorPins;
        if (pins.Count != 4)
            return "motorPins: четыре вывода, IN1–IN4";
        if (pins.Concat([r.LightPin]).Any(p => !FreePins.Contains(p)))
            return $"Свободные выводы ESP32-CAM: {string.Join(", ", FreePins)}";
        if (pins.Concat([r.LightPin]).Distinct().Count() != 5)
            return "Выводы моторов и фары не должны совпадать";
        if (r.MaxSpeed is < 10 or > 100)
            return "maxSpeed: от 10 до 100 %";
        if (r.MinSpeed is < 0 or > 90)
            return "minSpeed: от 0 до 90 %";
        return r.MaxRunS is < 1 or > 600 ? "maxRunS: от 1 до 600 с" : null;
    }

    public async Task<CarSetupDto?> GetAsync(string deviceId, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.TypeCode == Cars.Type, ct);
        if (device is null)
            return null;
        var root = device.Config?.RootElement;
        JsonElement? Section(string name) => root is { } r && r.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object ? e : null;
        var hw = Section("hw");
        var car = Section("car");
        bool Flag(JsonElement? e, string name, bool fallback) => e is { } x && x.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
        int Number(JsonElement? e, string name, int fallback) => e is { } x && x.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : fallback;
        IReadOnlyList<int>? pins = hw is { } h && h.TryGetProperty("motor_pins", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(x => x.GetInt32()).ToList()
            : null;
        return new CarSetupDto(pins, Flag(hw, "motor_pwm", true), Flag(car, "invert_left", false), Flag(car, "invert_right", false),
            Flag(car, "swap_sides", false), Number(car, "max_speed", 100), Number(car, "max_run_s", 60), Number(hw, "relay_pin", 4), Number(car, "min_speed", 40));
    }

    public async Task<ConfigUpdateStatus> SetAsync(string deviceId, CarSetupDto r, string by, CancellationToken ct)
    {
        if (Validate(r) is not null)
            return ConfigUpdateStatus.BadRequest;
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.TypeCode == Cars.Type, ct);
        if (device is null)
            return ConfigUpdateStatus.NotFound;
        var config = device.Config is { } current
            ? JsonNode.Parse(current.RootElement.GetRawText())!.AsObject()
            : new JsonObject { ["v"] = 1, ["name"] = device.Name, ["health_interval_s"] = 60 };
        var hw = config["hw"] as JsonObject ?? [];
        if (r.MotorPins is null) hw.Remove("motor_pins"); else hw["motor_pins"] = new JsonArray(r.MotorPins.Select(p => (JsonNode)p).ToArray());
        hw["motor_pwm"] = r.MotorPwm;
        hw["relay_pin"] = r.LightPin;
        config["hw"] = hw;
        var car = config["car"] as JsonObject ?? [];
        car["invert_left"] = r.InvertLeft;
        car["invert_right"] = r.InvertRight;
        car["swap_sides"] = r.SwapSides;
        car["max_speed"] = r.MaxSpeed;
        car["min_speed"] = r.MinSpeed;
        car["max_run_s"] = r.MaxRunS;
        config["car"] = car;
        device.ConfigRev++;
        config["rev"] = device.ConfigRev;
        device.Config = JsonDocument.Parse(config.ToJsonString());
        Audit.Record(db, time.GetUtcNow(), by, "car_setup", deviceId, r);
        await db.SaveChangesAsync(ct);
        if (mqtt.IsConnected)
            await mqtt.PublishAsync(Topics.Config(deviceId), config.ToJsonString(), retain: true, ct);
        return ConfigUpdateStatus.Updated;
    }
}

public static class CarEndpoints
{
    /// <summary>Per user: a held control sends 5 a second; room for a second window or a few clicks.</summary>
    public const string RateLimit = "car-drive";

    public static IServiceCollection AddCars(this IServiceCollection services)
    {
        services.AddSingleton<CarDrive>();
        services.AddScoped<CarSetup>();
        services.AddRateLimiter(o => o.AddPolicy(RateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromSeconds(1) })));
        return services;
    }

    public static IEndpointRouteBuilder MapCarEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/cars/{deviceId}/drive", async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> (
                string deviceId, DriveRequest r, ClaimsPrincipal user, CarDrive cars, CancellationToken ct) =>
                await cars.DriveAsync(deviceId, r, $"user:{user.Identity?.Name}", ct) switch
                {
                    DriveResult.Ok => TypedResults.NoContent(),
                    DriveResult.NotFound => TypedResults.NotFound(),
                    DriveResult.BadRequest => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [CarDrive.Invalid(r)!] }),
                    _ => TypedResults.Problem("MQTT broker is not connected", statusCode: StatusCodes.Status503ServiceUnavailable),
                })
            .WithTags("devices")
            .RequireAuthorization(HiveRoles.CanControl)
            .RequireRateLimiting(RateLimit);

        // Wiring and limits: the administrator's, like the pins of any hornet.
        app.MapGet("/api/cars/{deviceId}/setup", async Task<Results<Ok<CarSetupDto>, NotFound>> (string deviceId, CarSetup setup, CancellationToken ct) =>
                await setup.GetAsync(deviceId, ct) is { } dto ? TypedResults.Ok(dto) : TypedResults.NotFound())
            .WithTags("devices")
            .RequireAuthorization(HiveRoles.CanControl);
        app.MapPut("/api/cars/{deviceId}/setup", async Task<Results<Ok<CarSetupDto>, NotFound, ValidationProblem>> (
                string deviceId, CarSetupDto r, ClaimsPrincipal user, CarSetup setup, CancellationToken ct) =>
            {
                if (CarSetup.Validate(r) is { } error)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [error] });
                return await setup.SetAsync(deviceId, r, $"user:{user.Identity?.Name}", ct) == ConfigUpdateStatus.NotFound
                    ? TypedResults.NotFound()
                    : TypedResults.Ok((await setup.GetAsync(deviceId, ct))!);
            })
            .WithTags("devices")
            .RequireAuthorization(HiveRoles.AdminOnly);
        return app;
    }
}
