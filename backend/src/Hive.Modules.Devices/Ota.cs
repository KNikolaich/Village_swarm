using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Hive.Domain;
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

public sealed record StartOtaRequest(string Build);

public enum OtaStatus
{
    Sent,
    UnknownDevice,
    UnknownBuild,
    Refused,
    BrokerDown,
    /// <summary>Only checked: the build suits the hornet (used when an update is queued for a sleeper's next wake-up).</summary>
    Valid,
}

public sealed record OtaResult(OtaStatus Status, string? Error = null, CommandDto? Command = null, string? Version = null);

/// <summary>
/// Over-the-air update (spec 5.5): checks that the build suits the hornet, then sends it the "ota" command with the
/// download address, size, MD5 and version. The hornet downloads from /api/ota/{build}/firmware.bin with its own token.
/// </summary>
public sealed class OtaService(HiveDbContext db, CommandService commands, IOptions<ProvisioningOptions> options, IHostEnvironment env)
{
    /// <summary>Largest image the hive offers. An ESP8266 with 1 MB flash keeps the new image beside the old one: about 470 KB.</summary>
    public static long MaxImageBytes(string? board) => board is not null && board.StartsWith("esp-01", StringComparison.Ordinal) ? 460_000 : 1_800_000;

    /// <summary>The hive address for hornets: the one set in Settings, else the configured upload URL.</summary>
    private async Task<string?> HiveUrlAsync(CancellationToken ct) =>
        (await db.Modes.AsNoTracking().Where(m => m.Key == "hive.url").Select(m => m.Value).FirstOrDefaultAsync(ct))
        ?? options.Value.UploadUrl;

    /// <summary>
    /// A meteo or relay hornet may move to the heat firmware of its board: heat does all they do (thermometer, BME280,
    /// relay) and keeps the same pins, so a board with both a sensor and a relay needs no unsoldering to get both.
    /// The hornet reports its new type in info after the restart.
    /// </summary>
    public static bool CanBecome(string type, string role) => role == "heat" && type is ("meteo" or "relay");

    /// <summary>
    /// <paramref name="needOnline"/> false: a deep sleeper that has just woken (its status may not have caught up yet).
    /// <paramref name="checkOnly"/>: validate and stop before anything is sent.
    /// </summary>
    public async Task<OtaResult> StartAsync(string deviceId, string build, string by, CancellationToken ct, bool needOnline = true, bool checkOnly = false)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.Status != DeviceStatus.Disabled, ct);
        if (device is null)
            return new OtaResult(OtaStatus.UnknownDevice);
        if (ProvisioningEndpoints.ReadBuild(build, options.Value, env) is not { } b)
            return new OtaResult(OtaStatus.UnknownBuild);
        if (b.Role != device.TypeCode && !CanBecome(device.TypeCode, b.Role))
            return new OtaResult(OtaStatus.Refused, $"build {build} is for {b.Role}, not {device.TypeCode}");
        if (!string.Equals(b.Board, device.Hw, StringComparison.Ordinal))
            return new OtaResult(OtaStatus.Refused, $"build {build} is for board {b.Board}, this hornet reports {device.Hw}");
        if (b.Version is null)
            return new OtaResult(OtaStatus.Refused, "the build has no version: rebuild the firmware");
        if (b.Version == device.FwVersion)
            return new OtaResult(OtaStatus.Refused, $"already on {b.Version}");
        if (needOnline && device.Status != DeviceStatus.Online)
            return new OtaResult(OtaStatus.Refused, "the hornet is not online");

        var file = Path.Combine(b.Dir, "firmware.bin");
        var size = new FileInfo(file).Length;
        if (size > MaxImageBytes(device.Hw))
            return new OtaResult(OtaStatus.Refused, $"the image ({size / 1024} KB) does not fit this board's flash for an update");
        var hive = await HiveUrlAsync(ct);
        if (string.IsNullOrEmpty(hive))
            return new OtaResult(OtaStatus.Refused, "set the hive address in Settings first");

        if (checkOnly)
            return new OtaResult(OtaStatus.Valid, null, null, b.Version);

        var image = await File.ReadAllBytesAsync(file, ct);
        var md5 = Convert.ToHexStringLower(MD5.HashData(image));
        var payload = new JsonObject
        {
            ["url"] = $"{hive.TrimEnd('/')}/api/ota/{build}/firmware.bin",
            ["md5"] = md5,
            ["size"] = size,
            ["version"] = b.Version,
        };
        // A signed build (scripts/ota_sign.py made it): the signature goes along, after a check that it belongs to the
        // image on disk. A mismatch means the file changed since it was signed, and a hornet would refuse it anyway.
        if (b.Signature is { } signature)
        {
            if (b.Sha512 is not null && !string.Equals(b.Sha512, Convert.ToHexStringLower(SHA512.HashData(image)), StringComparison.Ordinal))
                return new OtaResult(OtaStatus.Refused, "the image does not match the signature in build.json: rebuild the firmware");
            if (signature.Length != 128 || !signature.All(Uri.IsHexDigit))
                return new OtaResult(OtaStatus.Refused, "build.json holds a malformed signature: rebuild the firmware");
            payload["sig"] = signature.ToLowerInvariant();
        }
        var (status, command) = await commands.SendAsync(deviceId, "ota", payload, by, 600, ct);
        return status switch
        {
            SendStatus.Sent => new OtaResult(OtaStatus.Sent, null, CommandDto.From(command!)),
            SendStatus.BrokerDown => new OtaResult(OtaStatus.BrokerDown),
            _ => new OtaResult(OtaStatus.UnknownDevice),
        };
    }

    /// <summary>The image a hornet may download: any enrolled hornet with its Bearer token, no session.</summary>
    public async Task<string?> AuthorizeDownloadAsync(string? bearer, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(bearer))
            return null;
        var hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bearer)));
        return await db.Devices.AsNoTracking().Where(d => d.UploadTokenHash == hash && d.Status != DeviceStatus.Disabled)
            .Select(d => d.DeviceId).FirstOrDefaultAsync(ct);
    }
}

public static class OtaEndpoints
{
    public static IEndpointRouteBuilder MapOtaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/devices/{deviceId}/ota", async Task<Results<Accepted<CommandDto>, NotFound, ValidationProblem, ProblemHttpResult>> (
                string deviceId, StartOtaRequest request, System.Security.Claims.ClaimsPrincipal user, OtaService ota, CancellationToken ct) =>
            {
                var r = await ota.StartAsync(deviceId, request.Build, $"user:{user.Identity?.Name}", ct);
                return r.Status switch
                {
                    OtaStatus.Sent => TypedResults.Accepted($"/api/devices/{deviceId}/commands/{r.Command!.Cid}", r.Command),
                    OtaStatus.UnknownDevice or OtaStatus.UnknownBuild => TypedResults.NotFound(),
                    OtaStatus.Refused => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["ota"] = [r.Error ?? "refused"] }),
                    _ => TypedResults.Problem("MQTT broker is not connected", statusCode: StatusCodes.Status503ServiceUnavailable),
                };
            })
            .WithTags("devices")
            .RequireAuthorization(HiveRoles.AdminOnly);

        // The hornet downloads here, with the Bearer token it got at enrollment (like photo uploads).
        app.MapGet("/api/ota/{build}/firmware.bin", async Task<Results<PhysicalFileHttpResult, UnauthorizedHttpResult, NotFound>> (
                string build, HttpContext http, OtaService ota, IOptions<ProvisioningOptions> options, IHostEnvironment env, CancellationToken ct) =>
            {
                var header = http.Request.Headers.Authorization.ToString();
                var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
                if (await ota.AuthorizeDownloadAsync(token, ct) is null)
                    return TypedResults.Unauthorized();
                return ProvisioningEndpoints.ReadBuild(build, options.Value, env) is { } b
                    ? TypedResults.PhysicalFile(Path.Combine(b.Dir, "firmware.bin"), "application/octet-stream")
                    : TypedResults.NotFound();
            })
            .AllowAnonymous()
            .WithTags("firmware")
            .ExcludeFromDescription();
        return app;
    }
}
