using System.Security.Claims;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Weather;

/// <summary>Weather forecast for the house (Open-Meteo): fetched in the background, stored, served, used by automation.</summary>
public static class WeatherModule
{
    public const string Name = "weather";

    public static IServiceCollection AddWeatherModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WeatherOptions>(configuration.GetSection(WeatherOptions.Section));
        services.AddSingleton<WeatherState>();
        services.AddHttpClient<OpenMeteoClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<WeatherSettingsService>();
        services.AddScoped<WeatherService>();
        services.AddHostedService<WeatherRefreshJob>();
        return services;
    }

    public static IEndpointRouteBuilder MapWeatherEndpoints(this IEndpointRouteBuilder app)
    {
        var weather = app.MapGroup("/api/weather").WithTags("weather");
        weather.MapGet("", (WeatherService w, CancellationToken ct) => w.GetAsync(ct));
        weather.MapPost("/refresh", async (WeatherService w, CancellationToken ct) =>
            {
                await w.RefreshAsync(ct);
                return await w.GetAsync(ct);
            })
            .RequireAuthorization(HiveRoles.AdminOnly);

        app.MapGroup("/api/settings/weather").WithTags("settings")
            .MapWeatherSettings();
        return app;
    }

    private static void MapWeatherSettings(this RouteGroupBuilder group)
    {
        group.MapGet("", (WeatherSettingsService s, CancellationToken ct) => s.GetAsync(ct))
            .RequireAuthorization(HiveRoles.AdminOnly);
        group.MapPut("", async Task<Results<Ok<WeatherDto>, ValidationProblem>> (
                WeatherSettingsDto request, ClaimsPrincipal user, WeatherSettingsService s, WeatherService w, CancellationToken ct) =>
            {
                if (WeatherSettingsService.Validate(request) is { } problem)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["weather"] = [problem] });
                await s.SetAsync(request, $"user:{user.Identity?.Name}", ct);
                // A new place: fetch now so the dashboard fills in without waiting for the next round.
                await w.RefreshAsync(ct);
                return TypedResults.Ok(await w.GetAsync(ct));
            })
            .RequireAuthorization(HiveRoles.AdminOnly);
    }
}

/// <summary>Refreshes the forecast every <see cref="WeatherOptions.RefreshMinutes"/> minutes, the first time shortly after start.</summary>
public sealed class WeatherRefreshJob(IServiceScopeFactory scopes, IOptions<WeatherOptions> options, TimeProvider time, ILogger<WeatherRefreshJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), time, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(5, options.Value.RefreshMinutes)), time);
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<WeatherService>().RefreshAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Weather refresh round failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
