namespace Hive.Modules.Weather;

/// <summary>Weather now. Wind speed is m/s, temperatures °C, precipitation mm, pressure hPa at sea level, clouds percent.</summary>
public sealed record WeatherCurrentDto(
    DateTimeOffset Time, double Temperature, double? Apparent, double? Humidity, double? Precipitation,
    int WeatherCode, double? WindSpeed, int? WindDirection, double? Pressure = null, int? CloudCover = null);

/// <summary>One hour. <see cref="PrecipitationProbability"/> is percent, null when the model has none for that hour.</summary>
public sealed record WeatherHourDto(
    DateTimeOffset Time, double Temperature, double? Apparent, int? PrecipitationProbability, double Precipitation,
    int WeatherCode, double? WindSpeed, int? WindDirection, double? Humidity, double? Pressure = null, int? CloudCover = null);

public sealed record WeatherDayDto(
    DateOnly Date, double TemperatureMin, double TemperatureMax, double PrecipitationSum, int? PrecipitationProbabilityMax,
    int WeatherCode, DateTimeOffset? Sunrise, DateTimeOffset? Sunset);

/// <summary>What is stored and served: now, hourly from two days back to a week ahead, and day summaries with sun times.</summary>
public sealed record WeatherForecast(
    WeatherCurrentDto? Current, IReadOnlyList<WeatherHourDto> Hourly, IReadOnlyList<WeatherDayDto> Daily);

/// <summary>
/// The answer of GET /api/weather. <see cref="Configured"/> is false until a place is set; <see cref="Error"/> is the
/// reason the last refresh failed (the older forecast, if any, is still served); <see cref="Stale"/> means that
/// forecast is more than three hours old, so decisions should not lean on it.
/// </summary>
public sealed record WeatherDto(
    bool Configured, DateTimeOffset? FetchedAt, bool Stale, string? Error,
    WeatherCurrentDto? Current, IReadOnlyList<WeatherHourDto> Hourly, IReadOnlyList<WeatherDayDto> Daily);

/// <summary>Where the weather is for and which Open-Meteo to ask (the public one or a proxy).</summary>
public sealed record WeatherSettingsDto(string BaseUrl, double? Latitude, double? Longitude, string Timezone);
