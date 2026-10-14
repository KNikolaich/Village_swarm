using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Hive.Tests.Integration;

/// <summary>The forecast of Open-Meteo reaches the hive: place and server are settings, a failure never blanks the last forecast.</summary>
[Collection(HiveCollection.Name)]
public sealed class WeatherTests(HiveFixture hive)
{
    [Fact]
    public async Task Setting_the_place_fetches_the_forecast_and_serves_it()
    {
        await using var meteo = await FakeOpenMeteo.StartAsync();
        var admin = await hive.LoginAsync();

        using var put = await admin.PutAsJsonAsync("/api/settings/weather", new { baseUrl = meteo.BaseUrl, latitude = 59.95, longitude = 31.05, timezone = "Europe/Moscow" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        Assert.Contains("latitude=59.95", meteo.LastQuery, StringComparison.Ordinal);
        Assert.Contains("past_days=2", meteo.LastQuery, StringComparison.Ordinal);
        var weather = await admin.GetFromJsonAsync<JsonElement>("/api/weather");
        Assert.True(weather.GetProperty("configured").GetBoolean());
        Assert.False(weather.GetProperty("stale").GetBoolean());
        Assert.Equal(6.7, weather.GetProperty("current").GetProperty("temperature").GetDouble());
        Assert.Equal(2, weather.GetProperty("hourly").GetArrayLength());
        var day = weather.GetProperty("daily")[0];
        Assert.Equal("2026-10-14", day.GetProperty("date").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-10-14T07:55:00+03:00"), day.GetProperty("sunrise").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_failing_weather_server_keeps_the_last_forecast_and_reports_the_reason()
    {
        await using var meteo = await FakeOpenMeteo.StartAsync();
        var admin = await hive.LoginAsync();
        using var put = await admin.PutAsJsonAsync("/api/settings/weather", new { baseUrl = meteo.BaseUrl, latitude = 59.95, longitude = 31.05, timezone = "Europe/Moscow" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        meteo.Fail = true;
        meteo.Temperature = -20;
        var failed = await (await admin.PostAsync("/api/weather/refresh", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Out of service", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(6.7, failed.GetProperty("current").GetProperty("temperature").GetDouble());

        meteo.Fail = false;
        var recovered = await (await admin.PostAsync("/api/weather/refresh", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, recovered.GetProperty("error").ValueKind);
        Assert.Equal(-20, recovered.GetProperty("current").GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task Bad_settings_are_refused_and_only_admins_change_them()
    {
        var admin = await hive.LoginAsync();
        using var badPlace = await admin.PutAsJsonAsync("/api/settings/weather", new { baseUrl = "https://api.open-meteo.com", latitude = 123, longitude = 31, timezone = "Europe/Moscow" });
        Assert.Equal(HttpStatusCode.BadRequest, badPlace.StatusCode);
        using var badUrl = await admin.PutAsJsonAsync("/api/settings/weather", new { baseUrl = "ftp://x", latitude = 59, longitude = 31, timezone = "Europe/Moscow" });
        Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);

        await hive.CreateUserAsync("viewer-weather", "viewer-weather-password", Hive.Infrastructure.Identity.HiveRoles.Viewer);
        var viewer = await hive.LoginAsync("viewer-weather", "viewer-weather-password");
        using var read = await viewer.GetAsync("/api/weather");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var settings = await viewer.GetAsync("/api/settings/weather");
        Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode);
        using var write = await viewer.PutAsJsonAsync("/api/settings/weather", new { baseUrl = "https://api.open-meteo.com", latitude = 59, longitude = 31, timezone = "Europe/Moscow" });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
