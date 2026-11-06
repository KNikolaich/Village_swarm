using System.Security.Claims;
using Hive.Infrastructure.Identity;
using Hive.Infrastructure.Mqtt;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Modules.Telemetry;

/// <summary>Ingest of hornet messages into PostgreSQL; later aggregates and retention (spec 6.2).</summary>
public static class TelemetryModule
{
    public const string Name = "telemetry";

    public static IServiceCollection AddTelemetryModule(this IServiceCollection services)
    {
        services.AddSingleton<IngestQueue>();
        services.AddSingleton<IMqttMessageHandler, NativeHornetAdapter>();
        services.TryAddSingleton<TasmotaPending>();
        services.AddSingleton<IMqttMessageHandler, TasmotaAdapter>();
        services.AddSingleton<IMqttMessageHandler, ZigbeeAdapter>();
        services.AddScoped<IngestWriter>();
        services.AddSingleton<IngestPipeline>();
        services.AddHostedService(sp => sp.GetRequiredService<IngestPipeline>());
        services.AddScoped<TelemetryQueries>();
        services.AddOptions<HiveSensorOptions>().BindConfiguration(HiveSensorOptions.Section);
        services.AddHostedService<HiveSensors>();
        services.AddOptions<OfflineWatchOptions>().BindConfiguration(OfflineWatchOptions.Section);
        services.AddHostedService<OfflineWatch>();
        services.AddScoped<MonitoringService>();
        services.AddSingleton<IIngestListener, MonitoringLimits>();
        services.AddSingleton<MonitoringWatch>();
        services.AddHostedService(sp => sp.GetRequiredService<MonitoringWatch>());
        return services;
    }

    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder app)
    {
        var telemetry = app.MapGroup("/api/telemetry").WithTags("telemetry");
        telemetry.MapGet("/latest", (string? device, TelemetryQueries q, CancellationToken ct) => q.LatestAsync(device, ct));
        telemetry.MapGet("/series", (string device, string metric, DateTimeOffset? from, DateTimeOffset? to, int? points, TelemetryQueries q, CancellationToken ct) =>
            q.SeriesAsync(device, metric, from, to, points, ct));
        telemetry.MapGet("/health", (string device, DateTimeOffset? from, DateTimeOffset? to, int? points, TelemetryQueries q, CancellationToken ct) =>
            q.HealthAsync(device, from, to, points, ct));
        telemetry.MapGet("/logs", (string device, string? level, int? limit, TelemetryQueries q, CancellationToken ct) =>
            q.LogsAsync(device, level, limit, ct));

        telemetry.MapGet("/monitoring", (MonitoringService m, CancellationToken ct) => m.ListAsync(ct));
        telemetry.MapPut("/monitoring/{deviceId}", async Task<Results<Ok<MonitoringDto>, NotFound, ValidationProblem>> (
                string deviceId, StartMonitoringRequest request, ClaimsPrincipal user, MonitoringService m, CancellationToken ct) =>
            {
                var (status, error, session) = await m.StartAsync(deviceId, request, $"user:{user.Identity?.Name}", ct);
                return status switch
                {
                    MonitoringStatus.NotFound => TypedResults.NotFound(),
                    MonitoringStatus.BadRequest => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["monitoring"] = [error ?? "bad request"] }),
                    _ => TypedResults.Ok(session!),
                };
            })
            .RequireAuthorization(HiveRoles.CanControl);
        telemetry.MapDelete("/monitoring/{deviceId}", async Task<Results<NoContent, NotFound>> (
                string deviceId, ClaimsPrincipal user, MonitoringService m, CancellationToken ct) =>
                await m.StopAsync(deviceId, $"user:{user.Identity?.Name}", ct) ? (Results<NoContent, NotFound>)TypedResults.NoContent() : TypedResults.NotFound())
            .RequireAuthorization(HiveRoles.CanControl);
        return app;
    }
}
