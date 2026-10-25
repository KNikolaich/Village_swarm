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

    [Fact]
    public void Placed_items_may_be_up_to_eight_and_must_fit_the_screen()
    {
        var oled = new DisplayScreenDto(72, 40);
        DisplayItemDto At(int x, int y, int size = 3, string? align = null) => new(null, "temperature", null, x, y, size, align);
        Assert.Null(DisplayText.Validate(Enumerable.Range(0, 8).Select(i => At(0, i * 5)).ToList(), oled));
        Assert.NotNull(DisplayText.Validate(Enumerable.Range(0, 9).Select(i => At(0, i * 4)).ToList(), oled));
        Assert.Null(DisplayText.Validate([At(71, 39, 10, "right")], oled));
        Assert.NotNull(DisplayText.Validate([At(72, 0)], oled));
        Assert.NotNull(DisplayText.Validate([At(0, 40)], oled));
        Assert.Null(DisplayText.Validate([At(500, 0)]));  // a board without a known screen: only the general limits
        Assert.NotNull(DisplayText.Validate([At(0, 0, 0)]));
        Assert.NotNull(DisplayText.Validate([At(0, 0, 11)]));
        Assert.NotNull(DisplayText.Validate([At(0, 0, 3, "middle")]));
        Assert.NotNull(DisplayText.Validate([At(-1, 0)]));
    }

    [Theory]
    [InlineData(1, 40, 6)]    // never below the smallest font
    [InlineData(3, 40, 12)]
    [InlineData(5, 40, 20)]
    [InlineData(10, 40, 40)]
    [InlineData(5, 480, 240)]  // the same size reads the same on a large panel
    [InlineData(1, 200, 20)]
    public void A_size_is_a_share_of_the_screen_height(int size, int height, int pixels) =>
        Assert.Equal(pixels, DisplayText.PixelHeight(size, height));
}
