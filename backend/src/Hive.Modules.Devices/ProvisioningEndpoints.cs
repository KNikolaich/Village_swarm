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

        // Images for the in-browser flasher (ESP Web Tools), one folder per build env (role-board).
        var firmware = app.MapGroup("/api/firmware").WithTags("firmware");
        firmware.MapGet("/{build}/manifest.json", Results<JsonHttpResult<JsonObject>, NotFound> (string build, IOptions<ProvisioningOptions> options, IHostEnvironment env) =>
        {
            if (ReadBuild(build, options.Value, env) is not { } b)
                return TypedResults.NotFound();
            var parts = new JsonArray(b.Parts.Select(p => (JsonNode)new JsonObject
            {
                ["path"] = $"/api/firmware/{build}/{p.File}",
                ["offset"] = p.Offset,
            }).ToArray());
            return TypedResults.Json(new JsonObject
            {
                ["name"] = $"Village Swarm {build}",
                ["version"] = b.Built.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                ["new_install_prompt_erase"] = true,
                ["builds"] = new JsonArray { new JsonObject { ["chipFamily"] = b.ChipFamily, ["parts"] = parts } },
            });
        });
        firmware.MapGet("/{build}/{file}", Results<PhysicalFileHttpResult, NotFound> (string build, string file, IOptions<ProvisioningOptions> options, IHostEnvironment env) =>
            ReadBuild(build, options.Value, env) is { } b && b.Parts.Any(p => p.File == file)
                ? TypedResults.PhysicalFile(Path.Combine(b.Dir, file), "application/octet-stream")
                : TypedResults.NotFound());
        firmware.MapGet("", (IOptions<ProvisioningOptions> options, IHostEnvironment env) =>
        {
            var root = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.FirmwareDir));
            if (!Directory.Exists(root))
                return Array.Empty<FirmwareBuildDto>();
            return Directory.GetDirectories(root)
                .Select(d => ReadBuild(Path.GetFileName(d), options.Value, env))
                .Where(b => b is not null)
                .Select(b => new FirmwareBuildDto(b!.Name, b.Role, b.Board, b.ChipFamily, b.Built))
                .OrderBy(b => b.Build, StringComparer.Ordinal)
                .ToArray();
        });
        return app;
    }

    private sealed record Part(string File, int Offset);

    private sealed record Build(string Name, string Dir, string Role, string Board, string ChipFamily, IReadOnlyList<Part> Parts, DateTime Built);

    /// <summary>firmware/dist/<build>/build.json (written by firmware/scripts/dist.py), or the classic ESP32 layout without it.</summary>
    private static Build? ReadBuild(string name, ProvisioningOptions options, IHostEnvironment env)
    {
        var dir = FirmwareDir(name, options, env);
        if (dir is null)
            return null;
        var role = name;
        var board = name;
        var chip = "ESP32";
        IReadOnlyList<Part> parts = Esp32Parts.Select(p => new Part(p.File, p.Offset)).ToList();
        var meta = Path.Combine(dir, "build.json");
        if (File.Exists(meta))
        {
            var json = JsonNode.Parse(File.ReadAllText(meta))!;
            role = json["role"]?.GetValue<string>() ?? role;
            board = json["board"]?.GetValue<string>() ?? board;
            chip = json["chipFamily"]?.GetValue<string>() ?? chip;
            parts = json["parts"]!.AsArray().Select(p => new Part(p!["file"]!.GetValue<string>(), p["offset"]!.GetValue<int>())).ToList();
        }
        if (!parts.All(p => File.Exists(Path.Combine(dir, p.File))))
            return null;
        return new Build(name, dir, role, board, chip, parts, File.GetLastWriteTimeUtc(Path.Combine(dir, "firmware.bin")));
    }

    private static string? FirmwareDir(string type, ProvisioningOptions options, IHostEnvironment env)
    {
        if (!Hive.Contracts.Mqtt.Topics.IsValidDeviceId(type))
            return null; // also blocks path tricks
        var dir = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.FirmwareDir, type));
        return Directory.Exists(dir) ? dir : null;
    }
}

/// <summary>A flashable firmware image: build env (role-board), role (device type), board name and chip.</summary>
public sealed record FirmwareBuildDto(string Build, string Role, string Board, string ChipFamily, DateTime Built);
