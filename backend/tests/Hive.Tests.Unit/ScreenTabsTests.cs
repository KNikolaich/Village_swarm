using Hive.Contracts.Messages;
using Hive.Modules.Devices;
using Hive.Modules.Weather;
using SkiaSharp;

namespace Hive.Tests.Unit;

/// <summary>The tabs of a panel with buttons (CrowPanel 3.7"): drawn by the hive, the tab bar where the panel looks for it.</summary>
public class ScreenTabsTests
{
    private static readonly DisplayScreenDto Landscape = new(416, 240, Rendered: true, Tabs: 3);
    private static readonly DisplayScreenDto Portrait = new(240, 416, Rendered: true, Tabs: 3);
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 20, 58, 0, TimeSpan.Zero);

    private static TabContext Context(int? battery = 64)
    {
        var hours = Enumerable.Range(0, 24 * 6).Select(h => new WeatherHourDto(
            new DateTimeOffset(Now.Date, TimeSpan.Zero).AddHours(h), 5 + 6 * Math.Sin(h / 4.0), null, h % 9 == 0 ? 60 : 0, h % 9 == 0 ? 1.2 : 0, h % 9 == 0 ? 61 : 2, 3 + h % 5, 200, 80)).ToList();
        var weather = new WeatherForecast(new WeatherCurrentDto(Now, 14.3, null, 82, 0, 2, 3.5, 250, 1015, 88), hours, []);
        return new TabContext(Now, t => t.ToOffset(TimeSpan.FromHours(3)), battery, weather,
            [new TabEvent(Now.AddHours(-3), "Движение", "Калитка"), new TabEvent(Now.AddDays(-1), "Дверь", "Сарай")]);
    }

    /// <summary>The families the tabs are written in, from the hive's own fonts.</summary>
    private static FontSet TabFonts()
    {
        SKTypeface Load(string name)
        {
            using var stream = typeof(ScreenTabs).Assembly.GetManifestResourceStream($"Hive.Modules.Devices.Fonts.{name}.ttf")!;
            return SKTypeface.FromStream(stream);
        }
        return new FontSet(new Dictionary<string, IReadOnlyDictionary<(bool, bool), SKTypeface>>
        {
            [ScreenTabs.TextFamily] = new Dictionary<(bool, bool), SKTypeface> { [(false, false)] = Load("Roboto-Regular"), [(true, false)] = Load("Roboto-Bold") },
            [ScreenTabs.LightFamily] = new Dictionary<(bool, bool), SKTypeface> { [(false, false)] = Load("Roboto-Light") },
            [ScreenTabs.SerifFamily] = new Dictionary<(bool, bool), SKTypeface> { [(false, false)] = Load("RobotoSerif-Regular"), [(true, false)] = Load("RobotoSerif-Bold") },
            [ScreenTabs.PixelFamily] = new Dictionary<(bool, bool), SKTypeface> { [(false, false)] = Load("PressStart2P-Regular") },
        });
    }

    private static SKBitmap Draw(int tab, TabContext? context = null, DisplayScreenDto? screen = null) =>
        ScreenTabs.Draw(tab, screen ?? Landscape, context ?? Context(), TabFonts(),
            new DisplayMessage { Ts = 0, Items = [new DisplayItem { Text = "21.5°", X = 10, Y = 10, Size = 3 }] }, null);

    private static int Black(SKBitmap b, SKRectI r)
    {
        var n = 0;
        for (var y = r.Top; y < r.Bottom; y++)
            for (var x = r.Left; x < r.Right; x++)
                if (b.GetPixel(x, y).Red < 128)
                    n++;
        return n;
    }

    [Theory]
    [InlineData(ScreenTabs.Forecast, false)]
    [InlineData(ScreenTabs.Measure, false)]
    [InlineData(ScreenTabs.Calendar, false)]
    [InlineData(ScreenTabs.Forecast, true)]
    [InlineData(ScreenTabs.Measure, true)]
    [InlineData(ScreenTabs.Calendar, true)]
    public void Every_tab_draws_above_the_bar_and_inverts_its_own_cell(int tab, bool standing)
    {
        var screen = standing ? Portrait : Landscape;
        using var bitmap = Draw(tab, screen: screen);
        Assert.Equal((screen.Width, screen.Height), (bitmap.Width, bitmap.Height));
        Assert.True(Black(bitmap, new SKRectI(0, 0, screen.Width, screen.Height - ScreenTabs.BarHeight)) > 300, "something is drawn");
        var top = screen.Height - ScreenTabs.BarHeight + 1;
        var cell = screen.Width / 3;
        for (var i = 0; i < 3; i++)
        {
            var r = new SKRectI(i * cell + 2, top, i * cell + cell - 2, screen.Height);
            var share = Black(bitmap, r) / (double)(r.Width * r.Height);
            if (i == tab)
                Assert.True(share > 0.6, $"tab {tab}: its cell is black");
            else
                Assert.True(share < 0.4, $"tab {tab}: cell {i} is white");
        }
    }

    [Fact]
    public void The_weather_keeps_its_temperature_inside_the_screen()
    {
        using var bitmap = Draw(ScreenTabs.Forecast);
        // the degree sign of the big temperature once ran off the top edge
        Assert.Equal(0, Black(bitmap, new SKRectI(150, 0, 310, 4)));
        Assert.True(Black(bitmap, new SKRectI(150, 4, 310, 46)) > 200, "the temperature is there");
    }

    [Fact]
    public void A_forecast_without_weather_says_so_instead_of_drawing_nothing()
    {
        using var bitmap = Draw(ScreenTabs.Forecast, Context() with { Weather = null });
        Assert.True(Black(bitmap, new SKRectI(0, 90, 416, 150)) > 100);
    }

    [Fact]
    public void Battery_follows_the_curve_of_a_lithium_cell()
    {
        Assert.Equal(0, ScreenTabs.BatteryPercent(3.2));
        Assert.Equal(50, ScreenTabs.BatteryPercent(3.8));
        Assert.Equal(100, ScreenTabs.BatteryPercent(4.25));
        Assert.InRange(ScreenTabs.BatteryPercent(3.725), 26, 39);
    }

    [Fact]
    public void The_temperature_scale_moves_with_the_forecast()
    {
        Assert.Equal((-5, 15, 5), ScreenTabs.Scale(-3.2, 14.1));
        Assert.Equal((2, 8, 2), ScreenTabs.Scale(2.4, 7.9));
        Assert.Equal((4, 6, 2), ScreenTabs.Scale(4, 4));
    }

    [Fact]
    public void Weather_codes_become_pictures()
    {
        Assert.Equal(WeatherIcons.Kind.Clear, WeatherIcons.Of(0));
        Assert.Equal(WeatherIcons.Kind.PartlyCloudy, WeatherIcons.Of(2));
        Assert.Equal(WeatherIcons.Kind.Rain, WeatherIcons.Of(81));
        Assert.Equal(WeatherIcons.Kind.Snow, WeatherIcons.Of(73));
        Assert.Equal(WeatherIcons.Kind.Thunder, WeatherIcons.Of(95));
        Assert.Equal(WeatherIcons.Kind.Fog, WeatherIcons.Of(45));
        Assert.Equal("Переменная облачность", WeatherIcons.Describe(2));
        Assert.Equal("Небольшие ливни", WeatherIcons.Describe(80));
    }

    [Fact]
    public void Standing_the_weather_shows_the_sun_of_today_beside_the_details()
    {
        var day = new WeatherDayDto(new DateOnly(2026, 10, 29), 3, 10, 1.2, 60, 2, new DateTimeOffset(2026, 10, 29, 4, 27, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 29, 14, 12, 0, TimeSpan.Zero));
        var context = Context();
        using var without = Draw(ScreenTabs.Forecast, context, Portrait);
        using var with = Draw(ScreenTabs.Forecast, context with { Weather = context.Weather! with { Daily = [day] } }, Portrait);
        var sun = new SKRectI(130, 112, 240, 162);
        Assert.Equal(0, Black(without, sun));
        Assert.True(Black(with, sun) > 100, "sunrise and sunset are drawn");
    }

    [Fact]
    public void The_drops_and_the_wind_are_inked_in_their_boxes()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(40, 20, SKColorType.Gray8, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            WeatherIcons.Drops(canvas, 0, 2, 16);
            WeatherIcons.Wind(canvas, 20, 2, 16);
        }
        Assert.InRange(Black(bitmap, new SKRectI(0, 0, 12, 20)), 30, 140);
        Assert.InRange(Black(bitmap, new SKRectI(20, 0, 40, 20)), 30, 200);
        Assert.Equal(0, Black(bitmap, new SKRectI(12, 0, 19, 20)));
    }

    [Fact]
    public void The_crowpanel_lies_landscape_unless_turned_otherwise()
    {
        Assert.Equal(270, DisplayScreens.DefaultRotation("crowpanel-epd37"));
        Assert.Equal(0, DisplayScreens.DefaultRotation("waveshare-s3-epaper-154"));
        Assert.Equal((416, 240, 3), DisplayScreens.For("crowpanel-epd37", 270) is { } s ? (s.Width, s.Height, s.Tabs) : default);
    }

    [Fact]
    public void Pixel_fonts_are_offered_at_their_grid_and_outline_fonts_at_whole_sizes()
    {
        Assert.Equal(new[] { 8, 16, 24, 32 }, DisplayFontCatalog.SizesOf("press-start").Take(4));
        Assert.Equal(new[] { 16, 32, 48 }, DisplayFontCatalog.SizesOf("pixellari").Take(3));
        Assert.Contains(12, DisplayFontCatalog.SizesOf("roboto"));
        Assert.Contains(36, DisplayFontCatalog.SizesOf("roboto-serif"));
        Assert.Equal(DisplayFontCatalog.OutlineSizes, DisplayFontCatalog.SizesOf("my-uploaded-font"));
        Assert.Equal(DisplayFontCatalog.OutlineSizes, DisplayFontCatalog.SizesOf(null));
    }
}
