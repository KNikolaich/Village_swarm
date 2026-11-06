using Hive.Modules.Weather;

namespace Hive.Tests.Unit.Weather;

public sealed class WeatherUnitTests
{
    private static readonly WeatherForecast Forecast = OpenMeteoClient.Parse(WeatherSample.Json);

    [Fact]
    public void Local_times_become_absolute_times_of_the_zone()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 13, 0, 0, TimeSpan.FromHours(3)), Forecast.Current!.Time);
        Assert.Equal(DateTimeOffset.Parse("2026-10-14T07:55:00+03:00"), Forecast.Daily[0].Sunrise);
    }

    [Fact]
    public void Current_hourly_and_daily_values_are_read()
    {
        Assert.Equal(6.7, Forecast.Current!.Temperature);
        Assert.Equal(71, Forecast.Current.Humidity);
        Assert.Equal(250, Forecast.Current.WindDirection);
        Assert.Equal(((double?)1015.3, (int?)88), (Forecast.Current.Pressure, Forecast.Current.CloudCover));
        Assert.Equal(((double?)1014.8, (int?)75), (Forecast.Hourly[1].Pressure, Forecast.Hourly[1].CloudCover));
        Assert.Equal((2.0, 8.0, 4.5), (Forecast.Daily[0].TemperatureMin, Forecast.Daily[0].TemperatureMax, Forecast.Daily[0].PrecipitationSum));
        Assert.Equal(90, Forecast.Daily[0].PrecipitationProbabilityMax);
    }

    [Fact]
    public void An_hour_without_a_temperature_is_skipped_not_invented()
    {
        Assert.Equal(3, Forecast.Hourly.Count);
        Assert.DoesNotContain(Forecast.Hourly, h => h.Time.Hour == 14);
    }

    [Fact]
    public void Request_asks_for_the_place_the_week_ahead_and_two_past_days()
    {
        var uri = OpenMeteoClient.BuildUri(new WeatherSettingsDto("https://proxy.example/om/", 59.9473, 31.03845, "Europe/Moscow"));
        Assert.StartsWith("https://proxy.example/om/v1/forecast?", uri.ToString(), StringComparison.Ordinal);
        var query = uri.Query;
        Assert.Contains("latitude=59.9473", query, StringComparison.Ordinal);
        Assert.Contains("longitude=31.03845", query, StringComparison.Ordinal);
        Assert.Contains("past_days=2", query, StringComparison.Ordinal);
        Assert.Contains("forecast_days=7", query, StringComparison.Ordinal);
        Assert.Contains("sunrise,sunset", query, StringComparison.Ordinal);
        Assert.Contains("pressure_msl,cloud_cover", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Rain_over_a_window_sums_millimetres_and_takes_the_highest_probability()
    {
        var (mm, probability) = ForecastMath.Rain(Forecast.Hourly, DateTimeOffset.Parse("2026-10-14T12:00:00+03:00"), DateTimeOffset.Parse("2026-10-14T14:00:00+03:00"));
        Assert.Equal(1.7, mm, 3);
        Assert.Equal(80, probability);
    }

    [Fact]
    public void Dark_is_after_sunset_and_before_sunrise_and_unknown_outside_the_data()
    {
        Assert.False(ForecastMath.IsDark(Forecast.Daily, DateTimeOffset.Parse("2026-10-14T12:00:00+03:00")));
        Assert.True(ForecastMath.IsDark(Forecast.Daily, DateTimeOffset.Parse("2026-10-14T20:00:00+03:00")));
        Assert.True(ForecastMath.IsDark(Forecast.Daily, DateTimeOffset.Parse("2026-10-15T05:00:00+03:00")));
        Assert.Null(ForecastMath.IsDark(Forecast.Daily, DateTimeOffset.Parse("2026-10-14T03:00:00+03:00")));
        Assert.Null(ForecastMath.IsDark(Forecast.Daily, DateTimeOffset.Parse("2026-10-20T12:00:00+03:00")));
    }

    [Theory]
    [InlineData(91, 31, "Europe/Moscow", false)]
    [InlineData(59.9, 181, "Europe/Moscow", false)]
    [InlineData(59.9, 31, "Mars/Olympus", false)]
    [InlineData(59.9, 31, "Europe/Moscow", true)]
    public void Settings_are_checked(double lat, double lon, string zone, bool ok) =>
        Assert.Equal(ok, WeatherSettingsService.Validate(new WeatherSettingsDto("https://api.open-meteo.com", lat, lon, zone)) is null);
}
