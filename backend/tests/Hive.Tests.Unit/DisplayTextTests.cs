using Hive.Modules.Devices;

namespace Hive.Tests.Unit;

/// <summary>What a reading looks like on the tiny screen, and which choices of lines are accepted.</summary>
public class DisplayTextTests
{
    [Theory]
    [InlineData("temperature", -3.24, "-3.2°")]
    [InlineData("temperature_2", 21.55, "21.6°")]
    [InlineData("humidity", 63.4, "63%")]
    [InlineData("battery_voltage", 3.94, "3.9V")]
    [InlineData("pressure", 1013.4, "1013")]
    [InlineData("lux", 12.34, "12.3")]
    public void A_reading_is_written_the_way_the_firmware_writes_its_own(string metric, double value, string text) =>
        Assert.Equal(text, DisplayText.Format(metric, value));

    [Fact]
    public void Up_to_four_lines_of_valid_metrics_and_devices_are_accepted()
    {
        Assert.Null(DisplayText.Validate([]));
        Assert.Null(DisplayText.Validate([new DisplayItemDto(null, "temperature", null), new DisplayItemDto("meteo-out1", "humidity", "Улица")]));
        Assert.NotNull(DisplayText.Validate(Enumerable.Repeat(new DisplayItemDto(null, "temperature", null), 5).ToList()));
        Assert.NotNull(DisplayText.Validate([new DisplayItemDto(null, "Temperature", null)]));
        Assert.NotNull(DisplayText.Validate([new DisplayItemDto("Bad Id", "temperature", null)]));
        Assert.NotNull(DisplayText.Validate([new DisplayItemDto(null, "temperature", new string('x', 17))]));
    }
}
