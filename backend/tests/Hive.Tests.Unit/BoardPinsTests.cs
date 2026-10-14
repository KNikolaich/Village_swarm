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
}
