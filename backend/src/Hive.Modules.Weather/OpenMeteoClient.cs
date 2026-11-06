using System.Globalization;
using System.Text.Json;

namespace Hive.Modules.Weather;

/// <summary>Reads a forecast from an Open-Meteo compatible server (api.open-meteo.com or a proxy of it).</summary>
public sealed class OpenMeteoClient(HttpClient http)
{
    private const string Current = "temperature_2m,apparent_temperature,relative_humidity_2m,precipitation,weathercode,windspeed_10m,winddirection_10m,pressure_msl,cloud_cover";
    private const string Hourly = "temperature_2m,apparent_temperature,precipitation_probability,precipitation,weathercode,windspeed_10m,winddirection_10m,relative_humidity_2m,pressure_msl,cloud_cover";
    private const string Daily = "weathercode,temperature_2m_max,temperature_2m_min,precipitation_sum,precipitation_probability_max,sunrise,sunset";

    /// <summary>Two past days (was it raining lately?) and a week ahead; wind in m/s.</summary>
    public static Uri BuildUri(WeatherSettingsDto s)
    {
        var inv = CultureInfo.InvariantCulture;
        var query = $"latitude={s.Latitude!.Value.ToString("0.#####", inv)}&longitude={s.Longitude!.Value.ToString("0.#####", inv)}"
                    + $"&timezone={Uri.EscapeDataString(s.Timezone)}&past_days=2&forecast_days=7&windspeed_unit=ms"
                    + $"&current={Current}&hourly={Hourly}&daily={Daily}";
        return new Uri($"{s.BaseUrl.TrimEnd('/')}/v1/forecast?{query}");
    }

    public async Task<WeatherForecast> FetchAsync(WeatherSettingsDto settings, CancellationToken ct)
    {
        using var response = await http.GetAsync(BuildUri(settings), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Weather service answered {(int)response.StatusCode}: {Reason(body)}");
        return Parse(body);
    }

    private static string Reason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("reason", out var reason))
                return reason.GetString() ?? "";
        }
        catch (JsonException)
        {
        }
        return body.Length > 120 ? body[..120] : body;
    }

    /// <summary>Open-Meteo answers in local times of the requested zone; they become absolute times here.</summary>
    public static WeatherForecast Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var offsetS = root.TryGetProperty("utc_offset_seconds", out var o) ? o.GetInt32() : 0;
        var zone = FindZone(root.TryGetProperty("timezone", out var z) ? z.GetString() : null);
        DateTimeOffset At(string local)
        {
            var dt = DateTime.Parse(local, CultureInfo.InvariantCulture, DateTimeStyles.None);
            return new DateTimeOffset(dt, zone?.GetUtcOffset(dt) ?? TimeSpan.FromSeconds(offsetS));
        }

        WeatherCurrentDto? current = null;
        if (root.TryGetProperty("current", out var c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("time", out var ct))
            current = new WeatherCurrentDto(
                At(ct.GetString()!), Num(c, "temperature_2m") ?? 0, Num(c, "apparent_temperature"), Num(c, "relative_humidity_2m"),
                Num(c, "precipitation"), (int)(Num(c, "weathercode") ?? 0), Num(c, "windspeed_10m"), Int(Num(c, "winddirection_10m")),
                Num(c, "pressure_msl"), Int(Num(c, "cloud_cover")));

        var hourly = new List<WeatherHourDto>();
        if (root.TryGetProperty("hourly", out var h))
        {
            var times = h.GetProperty("time").EnumerateArray().Select(t => t.GetString()!).ToList();
            for (var i = 0; i < times.Count; i++)
            {
                // A row with no temperature is a gap in the model output; skip it rather than invent a value.
                if (Num(h, "temperature_2m", i) is not { } temperature)
                    continue;
                hourly.Add(new WeatherHourDto(
                    At(times[i]), temperature, Num(h, "apparent_temperature", i), Int(Num(h, "precipitation_probability", i)),
                    Num(h, "precipitation", i) ?? 0, (int)(Num(h, "weathercode", i) ?? 0), Num(h, "windspeed_10m", i),
                    Int(Num(h, "winddirection_10m", i)), Num(h, "relative_humidity_2m", i),
                    Num(h, "pressure_msl", i), Int(Num(h, "cloud_cover", i))));
            }
        }

        var daily = new List<WeatherDayDto>();
        if (root.TryGetProperty("daily", out var d))
        {
            var days = d.GetProperty("time").EnumerateArray().Select(t => t.GetString()!).ToList();
            for (var i = 0; i < days.Count; i++)
            {
                if (Num(d, "temperature_2m_min", i) is not { } min || Num(d, "temperature_2m_max", i) is not { } max)
                    continue;
                daily.Add(new WeatherDayDto(
                    DateOnly.Parse(days[i], CultureInfo.InvariantCulture), min, max, Num(d, "precipitation_sum", i) ?? 0,
                    Int(Num(d, "precipitation_probability_max", i)), (int)(Num(d, "weathercode", i) ?? 0),
                    Text(d, "sunrise", i) is { } sr ? At(sr) : null, Text(d, "sunset", i) is { } ss ? At(ss) : null));
            }
        }
        return new WeatherForecast(current, hourly, daily);
    }

    private static TimeZoneInfo? FindZone(string? id)
    {
        try
        {
            return string.IsNullOrEmpty(id) ? null : TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static int? Int(double? v) => v is { } x ? (int)Math.Round(x) : null;

    private static double? Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static double? Num(JsonElement obj, string name, int index) =>
        obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array && index < arr.GetArrayLength()
        && arr[index].ValueKind == JsonValueKind.Number ? arr[index].GetDouble() : null;

    private static string? Text(JsonElement obj, string name, int index) =>
        obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array && index < arr.GetArrayLength()
        && arr[index].ValueKind == JsonValueKind.String ? arr[index].GetString() : null;
}
