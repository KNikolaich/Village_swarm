using Hive.Contracts.Messages;
using Hive.Modules.Devices;
using Hive.Modules.Weather;
using SkiaSharp;

namespace Hive.Tests.Unit;

/// <summary>The tabs of a panel with buttons (CrowPanel 3.7"): drawn by the hive, the tab bar where the panel looks for it.</summary>
public class ScreenTabsTests
{
    private static readonly DisplayScreenDto Landscape = new(416, 240, Rendered: true, Tabs: 4);
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 20, 58, 0, TimeSpan.Zero);

    private static TabContext Context(int? battery = 64)
    {
        var hours = Enumerable.Range(0, 24 * 6).Select(h => new WeatherHourDto(
            new DateTimeOffset(Now.Date, TimeSpan.Zero).AddHours(h), 5 + 6 * Math.Sin(h / 4.0), null, h % 9 == 0 ? 60 : 0, h % 9 == 0 ? 1.2 : 0, h % 9 == 0 ? 61 : 2, 3 + h % 5, 200, 80)).ToList();
        var weather = new WeatherForecast(new WeatherCurrentDto(Now, 14.3, null, 82, 0, 2, 3.5, 250, 1015, 88), hours, []);
        return new TabContext(Now, t => t.ToOffset(TimeSpan.FromHours(3)), battery, weather,
            [new TabEvent(Now.AddHours(-3), "Движение", "Калитка"), new TabEvent(Now.AddDays(-1), "Дверь", "Сарай")]);
    }

    private static SKBitmap Draw(int tab, TabContext? context = null) =>
        ScreenTabs.Draw(tab, Landscape, context ?? Context(), new FontSet(new Dictionary<string, IReadOnlyDictionary<(bool, bool), SKTypeface>>()),
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
    [InlineData(ScreenTabs.Common)]
    [InlineData(ScreenTabs.Forecast)]
    [InlineData(ScreenTabs.Calendar)]
    [InlineData(ScreenTabs.Measure)]
    public void Every_tab_draws_above_the_bar_and_inverts_its_own_cell(int tab)
    {
        using var bitmap = Draw(tab);
        Assert.Equal((416, 240), (bitmap.Width, bitmap.Height));
        Assert.True(Black(bitmap, new SKRectI(0, 0, 416, 240 - ScreenTabs.BarHeight)) > 300, "something is drawn");
        var top = 240 - ScreenTabs.BarHeight + 1;
        for (var cell = 0; cell < 4; cell++)
        {
            var r = new SKRectI(cell * 104 + 2, top, cell * 104 + 102, 240);
            var share = Black(bitmap, r) / (double)(r.Width * r.Height);
            if (cell == tab)
                Assert.True(share > 0.6, $"tab {tab}: its cell is black");
            else
                Assert.True(share < 0.4, $"tab {tab}: cell {cell} is white");
        }
    }

    [Fact]
    public void The_clock_shows_the_minute_so_the_picture_changes_with_it()
    {
        using var a = Draw(ScreenTabs.Common);
        using var b = Draw(ScreenTabs.Common, Context() with { Now = Now.AddMinutes(1) });
        Assert.NotEqual(DisplayRender.Pack(a), DisplayRender.Pack(b));
    }

    [Fact]
    public void The_fraktur_clock_stays_below_the_date_and_above_the_weekday()
    {
        using var stream = typeof(ScreenTabs).Assembly.GetManifestResourceStream("Hive.Modules.Devices.Fonts.UnifrakturCook-Bold.ttf")!;
        var fraktur = SKTypeface.FromStream(stream);
        var fonts = new FontSet(new Dictionary<string, IReadOnlyDictionary<(bool, bool), SKTypeface>>
        {
            [ScreenTabs.ClockFamily] = new Dictionary<(bool, bool), SKTypeface> { [(true, false)] = fraktur },
        });
        using var bitmap = ScreenTabs.Draw(ScreenTabs.Common, Landscape, Context(), fonts, new DisplayMessage { Ts = 0, Items = [] }, null);
        // the gap under the date's underline and the strip of the weekday's line (left of it) are clear of the digits
        Assert.Equal(0, Black(bitmap, new SKRectI(0, 39, 416, 44)));
        var content = 240 - ScreenTabs.BarHeight;
        Assert.Equal(0, Black(bitmap, new SKRectI(0, content - 20, 300, content)));
        Assert.True(Black(bitmap, new SKRectI(0, 44, 416, content - 20)) > 3000, "the clock is big");
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
    }

    [Fact]
    public void The_crowpanel_lies_landscape_unless_turned_otherwise()
    {
        Assert.Equal(270, DisplayScreens.DefaultRotation("crowpanel-epd37"));
        Assert.Equal(0, DisplayScreens.DefaultRotation("waveshare-s3-epaper-154"));
        Assert.Equal((416, 240, 4), DisplayScreens.For("crowpanel-epd37", 270) is { } s ? (s.Width, s.Height, s.Tabs) : default);
    }
}
