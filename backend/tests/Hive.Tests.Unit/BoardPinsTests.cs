using System.Text.Json;
using Hive.Modules.Devices;

namespace Hive.Tests.Unit;

/// <summary>Battery measurement (vbat) is part of the relay role too, so its pin is checked for conflicts there.</summary>
public class BoardPinsTests
{
    private static JsonElement Hw(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Relay_vbat_on_the_relay_pin_is_a_conflict()
    {
        var rows = BoardPins.Rows("nodemcu-v3", "relay", Hw("""{ "vbat_pin": 12 }"""));
        Assert.Contains(BoardPins.Conflicts("relay", rows), c => c.Contains("relay") && c.Contains("vbat"));
    }

    [Fact]
    public void Relay_vbat_on_a0_is_clean()
    {
        var rows = BoardPins.Rows("nodemcu-v3", "relay", Hw("""{ "vbat_pin": 17 }"""));
        Assert.Empty(BoardPins.Conflicts("relay", rows));
        Assert.Contains(rows, r => r.Function == "vbat" && r.Pin == 17);
    }

    [Fact]
    public void Camera_relay_defaults_to_the_flash_led_pin()
    {
        var rows = BoardPins.Rows("esp32cam-aithinker", "guard-cam", null);
        Assert.Contains(rows, r => r.Function == "relay" && r.Pin == 4 && r.Source == "default");
        Assert.Empty(BoardPins.Conflicts("guard-cam", rows, "esp32cam-aithinker"));
    }

    [Fact]
    public void Camera_relay_follows_the_config_pin()
    {
        var rows = BoardPins.Rows("esp32cam-aithinker", "guard-cam", Hw("""{ "relay_pin": 12 }"""));
        Assert.Contains(rows, r => r.Function == "relay" && r.Pin == 12 && r.Source == "config");
        Assert.Empty(BoardPins.Conflicts("guard-cam", rows, "esp32cam-aithinker"));
    }

    [Theory]
    [InlineData(13)] // PIR
    [InlineData(2)]  // SD data
    [InlineData(26)] // camera SCCB
    public void Camera_relay_on_a_board_pin_is_a_conflict(int pin)
    {
        var rows = BoardPins.Rows("esp32cam-aithinker", "guard-cam", Hw($$"""{ "relay_pin": {{pin}} }"""));
        Assert.Contains(BoardPins.Conflicts("guard-cam", rows, "esp32cam-aithinker"), c => c.StartsWith($"GPIO{pin}: relay"));
    }

    [Theory]
    [InlineData(5.7, true)]
    [InlineData(1.0, true)]
    [InlineData(0.0, false)]
    [InlineData(-2.0, false)]
    [InlineData(150.0, false)]
    public void Divider_ratio_range(double ratio, bool valid) => Assert.Equal(valid, BoardPins.ValidVbatRatio(ratio));

    [Fact]
    public void Divider_ratio_is_read_from_hw()
    {
        Assert.Equal(5.5, BoardPins.VbatRatio(Hw("""{ "vbat_pin": 17, "vbat_ratio": 5.5 }""")));
        Assert.Null(BoardPins.VbatRatio(Hw("""{ "vbat_pin": 17 }""")));
        Assert.Null(BoardPins.VbatRatio(null));
    }

    [Fact]
    public void A_meteo_hornet_shows_no_relay_pin()
    {
        var rows = BoardPins.Rows("esp-01", "meteo", null);
        Assert.DoesNotContain(rows, r => r.Function == "relay");
        Assert.Contains(rows, r => r.Function == "onewire");
    }

    [Fact]
    public void Heat_pins_follow_the_features()
    {
        var thermometer = BoardPins.Rows("esp-01", "heat", Hw("""{ "onewire_pin": 3 }"""), [DeviceFeatures.Thermometer]);
        Assert.Equal(["onewire"], thermometer.Select(r => r.Function));

        var lamp = BoardPins.Rows("esp-01", "heat", Hw("""{ "onewire_pin": 3 }"""), DeviceFeatures.Normalize([DeviceFeatures.Thermometer, DeviceFeatures.Heater]));
        Assert.Equal(["onewire", "relay"], lamp.Select(r => r.Function));
        Assert.Empty(BoardPins.Conflicts("heat", lamp, "esp-01", DeviceFeatures.Normalize([DeviceFeatures.Thermometer, DeviceFeatures.Heater])));
    }

    [Fact]
    public void Esp01_relay_and_bme280_share_gpio0()
    {
        string[] features = [DeviceFeatures.Bme280, DeviceFeatures.Relay];
        var rows = BoardPins.Rows("esp-01", "heat", null, features);
        Assert.Contains(BoardPins.Conflicts("heat", rows, "esp-01", features), c => c.StartsWith("GPIO0:"));
    }
}
