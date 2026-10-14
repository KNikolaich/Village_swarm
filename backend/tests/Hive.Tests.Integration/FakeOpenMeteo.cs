using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests.Integration;

/// <summary>Open-Meteo on a local port: one forecast, or an error answer when told to fail.</summary>
public sealed class FakeOpenMeteo : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string BaseUrl { get; private set; } = "";

    /// <summary>Last request's query string, to check what the hive asks for.</summary>
    public string LastQuery { get; private set; } = "";

    public bool Fail { get; set; }

    public double Temperature { get; set; } = 6.7;

    private FakeOpenMeteo(WebApplication app) => _app = app;

    public static async Task<FakeOpenMeteo> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeOpenMeteo(app);
        app.MapGet("/v1/forecast", (HttpContext context) =>
        {
            fake.LastQuery = context.Request.QueryString.Value ?? "";
            if (fake.Fail)
                return Results.Json(new { error = true, reason = "Out of service" }, statusCode: 500);
            return Results.Text(fake.Body(), "application/json");
        });
        await app.StartAsync();
        fake.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return fake;
    }

    private string Body() => $$"""
        {
          "utc_offset_seconds": 10800, "timezone": "Europe/Moscow",
          "current": { "time": "2026-10-14T13:00", "temperature_2m": {{Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "weathercode": 3, "windspeed_10m": 4.2 },
          "hourly": { "time": ["2026-10-14T12:00", "2026-10-14T13:00"], "temperature_2m": [6.0, 6.7], "precipitation": [0.0, 1.5],
                      "precipitation_probability": [20, 80], "weathercode": [3, 63] },
          "daily": { "time": ["2026-10-14"], "temperature_2m_max": [8.0], "temperature_2m_min": [2.0], "precipitation_sum": [4.5],
                     "weathercode": [63], "sunrise": ["2026-10-14T07:55"], "sunset": ["2026-10-14T17:44"] }
        }
        """;

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
