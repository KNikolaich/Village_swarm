using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Hive.Infrastructure.Data;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Devices;

public static class ProvisioningEndpoints
{
    // ESP32 (classic) flash layout of an Arduino build: what ESP Web Tools writes (spec 5.3 web flasher).
    private static readonly (string File, int Offset)[] Esp32Parts =
        [("bootloader.bin", 0x1000), ("partitions.bin", 0x8000), ("boot_app0.bin", 0xE000), ("firmware.bin", 0x10000)];

    public static IEndpointRouteBuilder MapProvisioningEndpoints(this IEndpointRouteBuilder app)
    {
        var provision = app.MapGroup("/api/provision").WithTags("provision");

        provision.MapPost("/codes", async Task<Results<Ok<EnrollmentCodeDto>, ValidationProblem>> (
                CreateCodeRequest request, ClaimsPrincipal user, ProvisioningService service, CancellationToken ct) =>
            {
                var (code, error) = await service.CreateCodeAsync(request, $"user:{user.Identity?.Name}", ct);
                return code is not null
                    ? TypedResults.Ok(code)
                    : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [error!] });
            })
            .RequireAuthorization(HiveRoles.AdminOnly);

        // Called by the hornet itself with the one-time code; no session (spec 6.5).
        provision.MapPost("/enroll", async Task<Results<Ok<EnrollResponse>, ForbidHttpResult, BadRequest>> (
                EnrollRequest request, HttpContext http, ProvisioningService service, CancellationToken ct) =>
            {
                var requestBase = new Uri($"{http.Request.Scheme}://{http.Request.Host}");
                var (status, response) = await service.EnrollAsync(request, requestBase, ct);
                return status switch
                {
                    EnrollStatus.Ok => TypedResults.Ok(response!),
                    EnrollStatus.BadCode => TypedResults.Forbid(),
                    _ => TypedResults.BadRequest(),
                };
            })
            .AllowAnonymous()
            .RequireRateLimiting("auth");

        app.MapPost("/api/devices/{deviceId}/replace", async Task<Results<Ok<EnrollmentCodeDto>, NotFound>> (
                string deviceId, ClaimsPrincipal user, HiveDbContext db, ProvisioningService service, CancellationToken ct) =>
            {
                var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
                if (device is null)
                    return TypedResults.NotFound();
                var (code, _) = await service.CreateCodeAsync(new CreateCodeRequest(device.DeviceId, device.TypeCode, device.Name, device.ZoneId), $"user:{user.Identity?.Name}", ct);
                return TypedResults.Ok(code!);
            })
            .WithTags("devices")
            .RequireAuthorization(HiveRoles.AdminOnly);

        app.MapDelete("/api/devices/{deviceId}", async Task<Results<NoContent, NotFound>> (
                string deviceId, ClaimsPrincipal user, ProvisioningService service, CancellationToken ct) =>
                await service.DisableAsync(deviceId, $"user:{user.Identity?.Name}", ct) ? TypedResults.NoContent() : TypedResults.NotFound())
            .WithTags("devices")
            .RequireAuthorization(HiveRoles.AdminOnly);

        // Images for the in-browser flasher (ESP Web Tools).
        var firmware = app.MapGroup("/api/firmware").WithTags("firmware");
        firmware.MapGet("/{type}/manifest.json", Results<JsonHttpResult<JsonObject>, NotFound> (string type, IOptions<ProvisioningOptions> options, IHostEnvironment env) =>
        {
            var dir = FirmwareDir(type, options.Value, env);
            if (dir is null || !Esp32Parts.All(p => File.Exists(Path.Combine(dir, p.File))))
                return TypedResults.NotFound();
            var built = File.GetLastWriteTimeUtc(Path.Combine(dir, "firmware.bin"));
            var parts = new JsonArray(Esp32Parts.Select(p => (JsonNode)new JsonObject
            {
                ["path"] = $"/api/firmware/{type}/{p.File}",
                ["offset"] = p.Offset,
            }).ToArray());
            return TypedResults.Json(new JsonObject
            {
                ["name"] = $"Village Swarm {type}",
                ["version"] = built.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                ["new_install_prompt_erase"] = true,
                ["builds"] = new JsonArray { new JsonObject { ["chipFamily"] = "ESP32", ["parts"] = parts } },
            });
        });
        firmware.MapGet("/{type}/{file}", Results<PhysicalFileHttpResult, NotFound> (string type, string file, IOptions<ProvisioningOptions> options, IHostEnvironment env) =>
        {
            var dir = FirmwareDir(type, options.Value, env);
            if (dir is null || !Esp32Parts.Any(p => p.File == file) || !File.Exists(Path.Combine(dir, file)))
                return TypedResults.NotFound();
            return TypedResults.PhysicalFile(Path.Combine(dir, file), "application/octet-stream");
        });
        firmware.MapGet("", (IOptions<ProvisioningOptions> options, IHostEnvironment env) =>
        {
            var root = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.FirmwareDir));
            return Directory.Exists(root)
                ? Directory.GetDirectories(root).Select(Path.GetFileName).Where(t => FirmwareDir(t!, options.Value, env) is not null).ToArray()
                : [];
        });
        return app;
    }

    private static string? FirmwareDir(string type, ProvisioningOptions options, IHostEnvironment env)
    {
        if (!Hive.Contracts.Mqtt.Topics.IsValidDeviceId(type))
            return null; // also blocks path tricks
        var dir = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.FirmwareDir, type));
        return Directory.Exists(dir) ? dir : null;
    }
}
