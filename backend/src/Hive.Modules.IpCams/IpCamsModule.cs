using System.Security.Claims;
using Hive.Infrastructure.Identity;
using Hive.Modules.Devices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Modules.IpCams;

/// <summary>IP cameras behind a Dahua NVR (contracts/ipcam.md): settings, "Проверить", live snapshots, the event stream.</summary>
public static class IpCamsModule
{
    public const string Name = "ipcams";

    public static IServiceCollection AddIpCamsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IpCamOptions>(configuration.GetSection(IpCamOptions.Section));
        services.AddSingleton<IpCamWatch>();
        services.AddHostedService(sp => sp.GetRequiredService<IpCamWatch>());
        services.AddSingleton<IDirectDriver>(sp => sp.GetRequiredService<IpCamWatch>());
        services.AddScoped<IpCamService>();
        services.AddSingleton<Snapshots>();
        services.AddSingleton<NvrLogins>();
        services.AddSingleton<IpCamDiagnostics>();
        services.TryAddSingleton<IFrameGrabber, FfmpegFrameGrabber>();
        return services;
    }

    public static IEndpointRouteBuilder MapIpCamEndpoints(this IEndpointRouteBuilder app)
    {
        var ipcams = app.MapGroup("/api/ipcams").WithTags("ipcams");

        ipcams.MapGet("", async Task<Ok<IReadOnlyList<NvrDto>>> (IpCamService s, CancellationToken ct) => TypedResults.Ok(await s.ListAsync(ct)))
            .RequireAuthorization(HiveRoles.AdminOnly);
        ipcams.MapGet("/{id:int}", async Task<Results<Ok<NvrDto>, NotFound>> (int id, IpCamService s, CancellationToken ct) =>
                await s.GetAsync(id, ct) is { } nvr ? TypedResults.Ok(nvr) : TypedResults.NotFound())
            .RequireAuthorization(HiveRoles.AdminOnly);

        // Before saving: the add form checks the address and the login it was given.
        ipcams.MapPost("/probe", async Task<Results<Ok<ProbeResultDto>, ValidationProblem>> (NvrProbeRequest r, NvrLogins logins, CancellationToken ct) =>
            {
                if (IpCamService.HostError(r.Host, r.Port) is { } error)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [error] });
                using var client = new DahuaClient(new NvrEndpoint(IpCamService.Host(r.Host), r.Port ?? 80, r.Username.Trim(), r.Password), logins);
                return TypedResults.Ok(await client.ProbeAsync(ct));
            })
            .RequireAuthorization(HiveRoles.AdminOnly);

        ipcams.MapPost("", async Task<Results<Ok<NvrDto>, ValidationProblem>> (NvrRequest r, ClaimsPrincipal user, IpCamService s, CancellationToken ct) =>
            {
                var (result, error, nvr) = await s.CreateAsync(r, $"user:{user.Identity?.Name}", ct);
                return result == NvrChange.Ok
                    ? TypedResults.Ok(nvr!)
                    : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [error!] });
            })
            .RequireAuthorization(HiveRoles.AdminOnly);

        ipcams.MapPut("/{id:int}", async Task<Results<Ok<NvrDto>, NotFound, ValidationProblem>> (
                int id, NvrRequest r, ClaimsPrincipal user, IpCamService s, CancellationToken ct) =>
            {
                var (result, error, nvr) = await s.UpdateAsync(id, r, $"user:{user.Identity?.Name}", ct);
                return result switch
                {
                    NvrChange.Ok => TypedResults.Ok(nvr!),
                    NvrChange.NotFound => TypedResults.NotFound(),
                    _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [error!] }),
                };
            })
            .RequireAuthorization(HiveRoles.AdminOnly);

        ipcams.MapDelete("/{id:int}", async Task<Results<NoContent, NotFound>> (int id, ClaimsPrincipal user, IpCamService s, CancellationToken ct) =>
                await s.DeleteAsync(id, $"user:{user.Identity?.Name}", ct) ? TypedResults.NoContent() : TypedResults.NotFound())
            .RequireAuthorization(HiveRoles.AdminOnly);

        ipcams.MapPost("/{id:int}/probe", async Task<Results<Ok<ProbeResultDto>, NotFound>> (int id, IpCamService s, NvrLogins logins, CancellationToken ct) =>
            {
                if (await s.EndpointAsync(id, ct) is not { } endpoint)
                    return TypedResults.NotFound();
                using var client = new DahuaClient(endpoint, logins);
                return TypedResults.Ok(await client.ProbeAsync(ct));
            })
            .RequireAuthorization(HiveRoles.AdminOnly);

        // A live frame, not stored: the settings page's "Снимок" and a look before picking a channel. Taken the same way as
        // photos (snapshot.cgi, then the RTSP stream); X-Snapshot-Via says which gave it, a 502 says why neither did.
        ipcams.MapGet("/{id:int}/snapshot", async Task<Results<FileContentHttpResult, NotFound, ProblemHttpResult>> (
                int id, int channel, HttpResponse response, IpCamService s, Snapshots snapshots, CancellationToken ct) =>
            {
                if (await s.EndpointAsync(id, ct) is not { } endpoint)
                    return TypedResults.NotFound();
                try
                {
                    var taken = await snapshots.TakeAsync(endpoint, await s.TargetAsync(id, channel, ct), SnapshotPurpose.Look, ct);
                    response.Headers["X-Snapshot-Via"] = taken.Via;
                    if (Hive.Modules.Media.ImageTools.Identify(taken.Jpeg) is { } size)
                        response.Headers["X-Snapshot-Size"] = $"{size.Width}x{size.Height}";
                    return TypedResults.File(taken.Jpeg, "image/jpeg");
                }
                catch (SnapshotFailedException ex)
                {
                    return TypedResults.Problem(ex.Message, title: "No snapshot", statusCode: StatusCodes.Status502BadGateway);
                }
            })
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg");
        return app;
    }
}
