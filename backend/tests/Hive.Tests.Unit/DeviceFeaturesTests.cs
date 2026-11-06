using System.Text.Json;
using Hive.Modules.Devices;

namespace Hive.Tests.Unit;

/// <summary>What a hornet is used for: the role's default until the config says otherwise, and what each role can do.</summary>
public class DeviceFeaturesTests
{
    private static JsonElement Config(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Without_features_the_role_default_applies()
    {
        Assert.Equal(["thermometer", "relay", "heater"], DeviceFeatures.Effective("heat", Config("""{ "v": 1 }""")));
        Assert.Equal(["thermometer", "bme280"], DeviceFeatures.Effective("meteo", null));
        Assert.Equal(["relay"], DeviceFeatures.Effective("relay", null));
        Assert.Null(DeviceFeatures.Effective("guard-cam", null));
    }

    [Fact]
    public void Config_features_are_read_normalized_and_limited_to_the_role()
    {
        Assert.Equal(["thermometer"], DeviceFeatures.Effective("heat", Config("""{ "features": ["thermometer"] }""")));
        Assert.Equal(["thermometer", "relay", "heater"], DeviceFeatures.Effective("heat", Config("""{ "features": ["heater", "thermometer"] }""")));
        Assert.Equal(["thermometer"], DeviceFeatures.Effective("meteo", Config("""{ "features": ["thermometer", "relay"] }""")));
        Assert.Empty(DeviceFeatures.Effective("heat", Config("""{ "features": [] }"""))!);
    }

    [Theory]
    [InlineData("heat", new[] { "thermometer", "relay", "heater" }, true)]
    [InlineData("heat", new[] { "relay" }, true)]
    [InlineData("heat", new[] { "relay", "heater" }, false)]   // a heater needs a sensor
    [InlineData("heat", new string[0], true)]                  // nothing wired: no heater, no relay, no sensors
    [InlineData("meteo", new string[0], true)]                 // e.g. a DHT build: the DS18B20/BME280 pins are left alone
    [InlineData("meteo", new[] { "relay" }, false)]            // the meteo firmware has no relay
    [InlineData("guard-cam", new[] { "relay" }, false)]
    public void Validation(string type, string[] features, bool ok) =>
        Assert.Equal(ok, DeviceFeatures.Validate(type, DeviceFeatures.Normalize(features).ToList()) is null);

    [Theory]
    [InlineData(5, 25, 21600, true)]
    [InlineData(20, 21, 3600, false)]  // max must be at least 2 °C above min
    [InlineData(5, 25, 30, false)]     // less than a minute
    [InlineData(-40, 25, 3600, false)]
    public void Heater_limits(double minC, double maxC, int maxOnS, bool ok) =>
        Assert.Equal(ok, DeviceFeatures.ValidateHeater(minC, maxC, maxOnS) is null);

    [Theory]
    [InlineData("meteo", "heat", true)]
    [InlineData("relay", "heat", true)]
    [InlineData("meteo", "relay", false)]
    [InlineData("heat", "meteo", false)]
    [InlineData("meteo", "monitor", true)]
    [InlineData("relay", "monitor", false)]
    [InlineData("monitor", "meteo", false)]
    public void Only_meteo_and_relay_hornets_move_to_the_heat_firmware_and_meteo_to_the_monitor_one(string type, string role, bool ok) =>
        Assert.Equal(ok, OtaService.CanBecome(type, role));
}
