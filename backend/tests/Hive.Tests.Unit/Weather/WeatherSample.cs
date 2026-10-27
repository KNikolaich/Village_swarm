namespace Hive.Tests.Unit.Weather;

/// <summary>A small Open-Meteo answer in the shape the real service (and its proxies) return.</summary>
public static class WeatherSample
{
    public const string Json = """
        {
          "latitude": 59.95, "longitude": 31.05, "utc_offset_seconds": 10800, "timezone": "Europe/Moscow",
          "current": { "time": "2026-10-14T13:00", "interval": 900, "temperature_2m": 6.7, "apparent_temperature": 3.1,
                       "relative_humidity_2m": 71, "precipitation": 0.0, "weathercode": 3, "windspeed_10m": 4.2, "winddirection_10m": 250,
                       "pressure_msl": 1015.3, "cloud_cover": 88 },
          "hourly": {
            "time": ["2026-10-14T11:00", "2026-10-14T12:00", "2026-10-14T13:00", "2026-10-14T14:00"],
            "temperature_2m": [5.0, 6.0, 6.7, null],
            "apparent_temperature": [2.0, 3.0, 3.1, null],
            "precipitation_probability": [10, 20, 80, null],
            "precipitation": [0.0, 0.2, 1.5, null],
            "weathercode": [3, 61, 63, null],
            "windspeed_10m": [3.0, 4.0, 4.2, null],
            "winddirection_10m": [240, 245, 250, null],
            "relative_humidity_2m": [70, 72, 71, null],
            "pressure_msl": [1014.0, 1014.8, 1015.3, null],
            "cloud_cover": [40, 75, 88, null]
          },
          "daily": {
            "time": ["2026-10-14", "2026-10-15"],
            "weathercode": [63, 3], "temperature_2m_max": [8.0, 7.0], "temperature_2m_min": [2.0, 1.0],
            "precipitation_sum": [4.5, 0.0], "precipitation_probability_max": [90, 20],
            "sunrise": ["2026-10-14T07:55", "2026-10-15T07:57"], "sunset": ["2026-10-14T17:44", "2026-10-15T17:42"]
          }
        }
        """;
}
