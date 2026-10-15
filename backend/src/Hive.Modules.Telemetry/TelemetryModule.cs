using Hive.Infrastructure.Mqtt;
using Hive.Modules.Telemetry.Ingest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Modules.Telemetry;

/// <summary>Ingest of hornet messages into PostgreSQL; later aggregates and retention (spec 6.2).</summary>
public static class TelemetryModule
{
    public const string Name = "telemetry";

    public static IServiceCollection AddTelemetryModule(this IServiceCollection services)
    {
        services.AddSingleton<IngestQueue>();
        services.AddSingleton<IMqttMessageHandler, NativeHornetAdapter>();
        services.AddScoped<IngestWriter>();
        services.AddSingleton<IngestPipeline>();
        services.AddHostedService(sp => sp.GetRequiredService<IngestPipeline>());
        services.AddScoped<TelemetryQueries>();
        services.AddOptions<HiveSensorOptions>().BindConfiguration(HiveSensorOptions.Section);
        services.AddHostedService<HiveSensors>();
        services.AddOptions<OfflineWatchOptions>().BindConfiguration(OfflineWatchOptions.Section);
        services.AddHostedService<OfflineWatch>();
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
        return app;
    }
}
