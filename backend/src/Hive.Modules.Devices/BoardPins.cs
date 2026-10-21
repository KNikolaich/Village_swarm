namespace Hive.Modules.Devices;

/// <summary>
/// Which pin does what on each board, as the firmware builds it (firmware/platformio.ini HW_* defaults). The hive
/// shows this per hornet and can override it with the config "hw" section; the firmware still has the final say.
/// </summary>
public static class BoardPins
{
    /// <summary>Thermometer: the sensor feature "thermometer" reads when the config "hw" does not say (DeviceFeatures.Ds18b20...).</summary>
    public sealed record Defaults(int? OneWire, int? Sda, int? Scl, int? Relay, int? Button, int? Dht, bool RelayActiveLow, string Note,
        string Thermometer = DeviceFeatures.Ds18b20);

    public sealed record PinRow(string Function, string Key, int? Pin, string Source, string? Note);

    private static readonly Dictionary<string, Defaults> Boards = new()
    {
        ["esp32cam-aithinker"] = new(null, null, null, 4, null, null, false, "PIR on GPIO13 (guard-cam, fixed); microSD in 1-bit mode; GPIO4 is the white flash LED"),
        ["esp32c3-mini"] = new(3, 8, 9, 10, null, null, false, "SuperMini: 1-Wire GPIO3, I2C GPIO8/GPIO9, relay GPIO10"),
        ["esp32c3-oled-042"] = new(3, 5, 6, null, null, null, false, "0,42\" OLED on the same I2C bus (GPIO5/GPIO6)"),
        ["wemos-d1-mini"] = new(14, 4, 5, 12, 0, null, false, "D5/D2/D1 for 1-Wire/I2C, D6 relay, D3 button"),
        ["nodemcu-v3"] = new(14, 4, 5, 12, 0, null, false, "as Wemos D1 mini; D3 is the FLASH button"),
        ["esp-01"] = new(2, 0, 2, 0, null, null, true, "GPIO2 is shared by 1-Wire and I2C SCL; wire one kind of sensor only"),
        ["esp-01-dht11"] = new(null, null, null, null, null, 2, false, "ESP-01S DHT11 module: DHT11 on GPIO2, nothing else", DeviceFeatures.Dht11),
    };

    /// <summary>Roles and the functions they use: a pin is checked for conflicts only among these.</summary>
    private static readonly Dictionary<string, string[]> RoleFunctions = new()
    {
        ["meteo"] = ["onewire", "sda", "scl", "vbat", "dht"],
        ["relay"] = ["relay", "button", "vbat"],
        ["heat"] = ["onewire", "sda", "scl", "relay", "button", "vbat", "dht"],
        ["guard-cam"] = ["relay"],
    };

    /// <summary>
    /// Whether the hornet uses a pin function: its role has it and, where the role has features
    /// (<see cref="DeviceFeatures"/>), one of the chosen features needs it. An unknown role uses everything.
    /// </summary>
    public static bool Uses(string type, IReadOnlyCollection<string>? features, string function) =>
        (!RoleFunctions.TryGetValue(type, out var fns) || fns.Contains(function))
        && (features is null || DeviceFeatures.Uses(features, function));

    /// <summary>Pins a board keeps for itself; the firmware refuses them for any function (guard_cam.cpp outputPinAllowed).</summary>
    private static readonly Dictionary<string, (int[] Pins, string Owner)> Reserved = new()
    {
        ["esp32cam-aithinker"] = ([0, 2, 5, 13, 14, 15, 16, 17, 18, 19, 21, 22, 23, 25, 26, 27, 32, 34, 35, 36, 37, 38, 39], "camera, SD, PSRAM or PIR"),
    };

    public static bool KnownBoard(string board) => Boards.ContainsKey(board);

    public static Defaults? For(string? board) => board is not null && Boards.TryGetValue(board, out var d) ? d : null;

    /// <summary>Functions that do nothing without a pin: shown as not set (Source "none") instead of being left out.</summary>
    private static readonly string[] NeedPin = ["onewire", "sda", "scl", "relay", "dht"];

    /// <summary>
    /// The sensor the feature "thermometer" reads: config "hw".thermometer when set, else the board's (a DHT11 on the
    /// esp-01-dht11 module, a DS18B20 elsewhere).
    /// </summary>
    public static string ThermometerSensor(string? board, System.Text.Json.JsonElement? hw) =>
        ThermometerByHand(hw) is { } s ? s : For(board)?.Thermometer ?? DeviceFeatures.Ds18b20;

    /// <summary>The config "hw".thermometer, or null when the board default applies.</summary>
    public static string? ThermometerByHand(System.Text.Json.JsonElement? hw) =>
        hw is { ValueKind: System.Text.Json.JsonValueKind.Object } h && h.TryGetProperty("thermometer", out var v)
        && v.ValueKind == System.Text.Json.JsonValueKind.String && DeviceFeatures.ThermometerSensors.Contains(v.GetString())
            ? v.GetString() : null;

    /// <summary>
    /// Effective pins of the functions the hornet uses: the config "hw" override where given, else the board default.
    /// A pin the hornet needs but neither gives is listed with no pin (Source "none"), so a missing default shows.
    /// The thermometer uses the 1-Wire pin for DS18B20s and the DHT pin for a DHT11/DHT22, never both.
    /// </summary>
    public static IReadOnlyList<PinRow> Rows(string? board, string type, System.Text.Json.JsonElement? hw, IReadOnlyCollection<string>? features = null)
    {
        var dht = ThermometerSensor(board, hw) != DeviceFeatures.Ds18b20;
        var d = For(board) ?? new Defaults(null, null, null, null, null, null, false, "unknown board: pins are whatever the config says");
        int? Pick(string key, int? fallback) =>
            hw is { ValueKind: System.Text.Json.JsonValueKind.Object } h && h.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                ? v.GetInt32() : fallback;
        var rows = new List<PinRow>
        {
            new("onewire", "onewire_pin", Pick("onewire_pin", d.OneWire), Source(hw, "onewire_pin"), "DS18B20"),
            new("sda", "sda_pin", Pick("sda_pin", d.Sda), Source(hw, "sda_pin"), "I2C SDA (BME280, OLED)"),
            new("scl", "scl_pin", Pick("scl_pin", d.Scl), Source(hw, "scl_pin"), "I2C SCL"),
            new("relay", "relay_pin", Pick("relay_pin", d.Relay), Source(hw, "relay_pin"), d.RelayActiveLow ? "active low" : "active high"),
            new("button", "button_pin", Pick("button_pin", d.Button), Source(hw, "button_pin"), "local toggle"),
            new("vbat", "vbat_pin", Pick("vbat_pin", null), Source(hw, "vbat_pin"), "battery divider (ADC)"),
            new("dht", "dht_pin", Pick("dht_pin", d.Dht), Source(hw, "dht_pin"), "DHT11/22 data"),
        };
        return rows
            .Where(r => (r.Pin is not null || NeedPin.Contains(r.Function)) && Uses(type, features, r.Function))
            .Where(r => r.Function switch { "onewire" => !dht, "dht" => dht, _ => true })
            .Select(r => r.Pin is null ? r with { Source = "none" } : r)
            .ToList();
    }

    private static string Source(System.Text.Json.JsonElement? hw, string key) =>
        hw is { ValueKind: System.Text.Json.JsonValueKind.Object } h && h.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
            ? "config" : "default";

    /// <summary>Pins used twice by the functions a role uses, or taken by the board itself; empty when the wiring is clean.</summary>
    public static IReadOnlyList<string> Conflicts(string type, IReadOnlyList<PinRow> rows, string? board = null, IReadOnlyCollection<string>? features = null)
    {
        var mine = rows.Where(r => RoleFunctions.ContainsKey(type) && Uses(type, features, r.Function) && r.Pin is not null).ToList();
        var twice = mine.GroupBy(r => r.Pin!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => $"GPIO{g.Key}: {string.Join(", ", g.Select(r => r.Function))}");
        var taken = board is not null && Reserved.TryGetValue(board, out var res)
            ? mine.Where(r => res.Pins.Contains(r.Pin!.Value)).Select(r => $"GPIO{r.Pin}: {r.Function} ({res.Owner})")
            : [];
        return twice.Concat(taken).ToList();
    }

    public static bool ValidPin(int pin) => pin is >= 0 and <= 39;

    /// <summary>Divider ratio (R1+R2)/R2: 5.7 for 47k over 10k. Above 100 the ADC step would be volts, not millivolts.</summary>
    public static bool ValidVbatRatio(double ratio) => ratio > 0 && ratio <= 100 && double.IsFinite(ratio);

    /// <summary>The config "hw".vbat_ratio, or null when the firmware default (1.0) applies.</summary>
    public static double? VbatRatio(System.Text.Json.JsonElement? hw) =>
        hw is { ValueKind: System.Text.Json.JsonValueKind.Object } h && h.TryGetProperty("vbat_ratio", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
            ? v.GetDouble() : null;
}
