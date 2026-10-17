using System.Text.Json;

namespace Hive.Modules.Devices;

/// <summary>
/// What a hornet is used for (config "features", contracts/schemas/config.schema.json): thermometer, bme280, relay,
/// heater. The roles meteo, relay and heat can be told which of their abilities are wired; the hive then shows only
/// those pins, readings and commands. heat is the universal ESP8266 firmware that can do all four.
/// </summary>
public static class DeviceFeatures
{
    public const string Thermometer = "thermometer";
    public const string Bme280 = "bme280";
    public const string Relay = "relay";
    public const string Heater = "heater";

    /// <summary>Failsafe defaults the firmware uses when the config has no failsafe.heater (HeaterFailsafe.h).</summary>
    public const double DefaultMinC = 5.0;
    public const double DefaultMaxC = 25.0;
    public const int DefaultMaxOnS = 21600;

    private static readonly Dictionary<string, string[]> Supported = new()
    {
        ["meteo"] = [Thermometer, Bme280],
        ["relay"] = [Relay],
        ["heat"] = [Thermometer, Bme280, Relay, Heater],
    };

    private static readonly Dictionary<string, string[]> Defaults = new()
    {
        ["meteo"] = [Thermometer, Bme280],
        ["relay"] = [Relay],
        ["heat"] = [Thermometer, Relay, Heater],
    };

    /// <summary>The pin functions each feature uses (BoardPins rows).</summary>
    private static readonly Dictionary<string, string[]> Functions = new()
    {
        ["onewire"] = [Thermometer],
        ["sda"] = [Bme280],
        ["scl"] = [Bme280],
        ["relay"] = [Relay],
        ["button"] = [Relay],
    };

    /// <summary>Features the role's firmware knows, or null for a role without any (guard-cam, Tasmota devices...).</summary>
    public static IReadOnlyList<string>? SupportedBy(string type) => Supported.GetValueOrDefault(type);

    /// <summary>What the hornet is used for: the config "features" when set, else the role's default; null when the role has none.</summary>
    public static IReadOnlyList<string>? Effective(string type, JsonElement? config)
    {
        if (!Supported.TryGetValue(type, out var known))
            return null;
        if (config is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty("features", out var f) && f.ValueKind == JsonValueKind.Array)
            return Normalize(f.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => known.Contains(x)));
        return Defaults[type];
    }

    /// <summary>A heater switches the relay, so it implies one. Kept in a fixed order.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> features)
    {
        var set = features.ToHashSet();
        if (set.Contains(Heater))
            set.Add(Relay);
        return new[] { Thermometer, Bme280, Relay, Heater }.Where(x => set.Contains(x)).ToList();
    }

    /// <summary>Whether a pin function is used with these features; functions no feature owns (vbat, dht) always are.</summary>
    public static bool Uses(IReadOnlyCollection<string> features, string function) =>
        !Functions.TryGetValue(function, out var owners) || owners.Any(o => features.Contains(o));

    /// <summary>Why this set of features cannot be used by the role, or null when it can.</summary>
    public static string? Validate(string type, IReadOnlyCollection<string> features)
    {
        if (!Supported.TryGetValue(type, out var known))
            return $"тип {type} не настраивается";
        if (features.Count == 0)
            return "выберите хотя бы одно назначение";
        if (features.FirstOrDefault(f => !known.Contains(f)) is { } unknown)
            return $"прошивка {type} не умеет «{unknown}»";
        if (features.Contains(Heater) && !features.Contains(Thermometer) && !features.Contains(Bme280))
            return "обогреву нужен датчик температуры (DS18B20 или BME280)";
        return null;
    }

    /// <summary>Heater limits a person may set: the hive can narrow the failsafe, not make it meaningless.</summary>
    public static string? ValidateHeater(double minC, double maxC, int maxOnS)
    {
        if (minC is < -30 or > 40)
            return "минимум: от -30 до 40 °C";
        if (maxC > 60 || maxC < minC + 2)
            return "максимум: не выше 60 °C и хотя бы на 2 °C выше минимума";
        if (maxOnS is < 60 or > 86400)
            return "дольше всего: от 1 минуты до 24 часов";
        return null;
    }
}
