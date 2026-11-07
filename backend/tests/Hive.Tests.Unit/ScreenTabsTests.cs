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
            [ScreenTabs.PixelFamily] = new Dictionary<(bool, bool), SKTypeface> { [(false, false)] = Load("PublicPixel") },
            [ScreenTabs.BookFamily] = new Dictionary<(bool, bool), SKTypeface> { [(false, false)] = Load("NotoSerif-Regular"), [(false, true)] = Load("NotoSerif-Italic") },
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
        var edges = ScreenTabs.CellEdges(screen.Width);
        for (var i = 0; i < 3; i++)
        {
            var r = new SKRectI(edges[i] + 2, top, edges[i + 1] - 2, screen.Height);
            var share = Black(bitmap, r) / (double)(r.Width * r.Height);
            if (i == tab)
                Assert.True(share > 0.6, $"tab {tab}: its cell is black");
            else
                Assert.True(share < 0.4, $"tab {tab}: cell {i} is white");
        }
    }

    [Fact]
    public void The_calendar_shows_each_event_once_newest_first()
    {
        var at = new DateTimeOffset(2026, 10, 28, 11, 54, 10, TimeSpan.Zero);
        var events = ScreenTabs.Distinct(
        [
            new TabEvent(at, "Движение", "Top zoom30 front"),
            new TabEvent(at.AddSeconds(20), "Движение", "Top zoom30 front"),
            new TabEvent(at.AddSeconds(40), "Движение", "Top zoom30 front"),
            new TabEvent(at.AddSeconds(30), "Движение", "dahua4"),
            new TabEvent(at.AddMinutes(-5), "Движение", "Top zoom30 front"),
        ], t => t.ToOffset(TimeSpan.FromHours(3)));
        Assert.Equal(3, events.Count);
        Assert.Equal(at.AddSeconds(40), events[0].At);
        Assert.Equal("dahua4", events[1].Where);
        Assert.Equal(at.AddMinutes(-5), events[2].At);
    }

    [Fact]
    public void Standing_the_calendar_fills_down_to_the_tabs_with_news_and_leaves_them_clear()
    {
        var many = Enumerable.Range(0, 30).Select(i => new TabEvent(Now.AddMinutes(-7 * i), "Движение", "Калитка")).ToList();
        using var bitmap = Draw(ScreenTabs.Calendar, Context() with { Events = many }, Portrait);
        var barTop = Portrait.Height - ScreenTabs.BarHeight;
        // the news reach the lowest fifth of the screen above the tabs
        Assert.True(Black(bitmap, new SKRectI(0, barTop - 40, Portrait.Width, barTop - 4)) > 100, "news down to the tabs");
        // and leave the line above the tab bar white
        Assert.Equal(0, Black(bitmap, new SKRectI(0, barTop - 2, Portrait.Width, barTop - 1)));
    }

    [Fact]
    public void Inverting_swaps_black_and_white()
    {
        using var bitmap = Draw(ScreenTabs.Calendar);
        var black = Black(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
        DisplayRender.Invert(bitmap);
        Assert.Equal(bitmap.Width * bitmap.Height - black, Black(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height)));
    }

    [Fact]
    public void The_cells_of_the_tab_bar_follow_their_weights_and_cover_the_screen()
    {
        Assert.Equal("2,3,3", ScreenTabs.TabWeightsHeader);
        Assert.Equal(new[] { 0, 60, 150, 240 }, ScreenTabs.CellEdges(240));
        Assert.Equal(new[] { 0, 104, 260, 416 }, ScreenTabs.CellEdges(416));
    }

    [Theory]
    [InlineData(ScreenTabs.Forecast)]
    [InlineData(ScreenTabs.Measure)]
    public void Weather_and_measure_say_when_they_were_drawn_in_the_corner(int tab)
    {
        using var bitmap = Draw(tab);
        // the small print of the pixel font, at the bottom right above the tabs
        Assert.True(Black(bitmap, new SKRectI(300, Landscape.Height - ScreenTabs.BarHeight - 12, 416, Landscape.Height - ScreenTabs.BarHeight)) > 40, "the time of the picture");
    }

    [Fact]
    public void The_graph_starts_now_and_runs_two_days_past_today()
    {
        // late in the evening little is left of today: its column is narrow and goes without a date, the next two days
        // still fill the graph to the right edge
        var context = Context();
        using var evening = Draw(ScreenTabs.Forecast, context with { Now = new DateTimeOffset(2026, 10, 29, 20, 58, 0, TimeSpan.Zero) }, Portrait);
        using var morning = Draw(ScreenTabs.Forecast, context with { Now = new DateTimeOffset(2026, 10, 29, 3, 0, 0, TimeSpan.Zero) }, Portrait);
        foreach (var bitmap in new[] { evening, morning })
        {
            Assert.True(Black(bitmap, new SKRectI(0, 210, 240, 394)) > 300, "a graph is drawn");
            Assert.True(Black(bitmap, new SKRectI(200, 210, 240, 394)) > 40, "the third day reaches the right edge");
        }
        Assert.NotEqual(Black(evening, new SKRectI(0, 200, 240, 394)), Black(morning, new SKRectI(0, 200, 240, 394)));
    }

    [Fact]
    public void The_weather_in_words_stands_on_the_dotted_line_and_erases_it_under_the_words()
    {
        using var bitmap = Draw(ScreenTabs.Forecast, screen: Portrait);
        // the dotted line at y 111 starts at the left edge; the words end at its right end and wipe the line under them
        Assert.True(Black(bitmap, new SKRectI(4, 110, 20, 113)) > 0, "the line is at the left edge");
        Assert.True(Black(bitmap, new SKRectI(150, 101, 236, 121)) > 40, "the words are pressed to the right end");
        Assert.True(Black(bitmap, new SKRectI(236, 101, 240, 121)) == 0, "a margin is left at the right edge");
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

    // ---- the small panel (200x200, no tabs): the battery in the corner of the picture the hive draws

    private static readonly DisplayScreenDto Small = new(200, 200, Rendered: true);

    private static SKBitmap Blank() { var b = new SKBitmap(new SKImageInfo(200, 200, SKColorType.Gray8, SKAlphaType.Opaque)); b.Erase(SKColors.White); return b; }

    [Fact]
    public void The_small_panel_gets_its_battery_in_the_top_right_corner_and_nothing_elsewhere()
    {
        using var bare = Blank();
        using var drawn = Blank();
        ScreenTabs.DrawBatteryCorner(drawn, TabFonts(), 64);
        // the corner: the icon and the percent in the top 30 rows, right 70 columns
        var corner = new SKRectI(130, 0, 200, 30);
        Assert.Equal(0, Black(bare, corner));
        Assert.True(Black(drawn, corner) > 40, "battery and percent are drawn");
        // not a pixel outside it: the rest of the picture (the person's items) is untouched
        Assert.Equal(0, Black(drawn, new SKRectI(0, 30, 200, 200)));
        Assert.Equal(0, Black(drawn, new SKRectI(0, 0, 130, 30)));
    }

    [Fact]
    public void An_unknown_battery_draws_nothing_and_the_percent_goes_in_steps_of_five()
    {
        using var drawn = Blank();
        ScreenTabs.DrawBatteryCorner(drawn, TabFonts(), null);
        Assert.Equal(0, Black(drawn, new SKRectI(0, 0, 200, 200)));
        Assert.Equal([0, 0, 5, 65, 65, 70, 100, 100], new[] { 0, 2, 3, 64, 66, 68, 99, 100 }.Select(ScreenTabs.CornerPercent));
        using var full = Blank();
        using var half = Blank();
        ScreenTabs.DrawBatteryCorner(full, TabFonts(), 100);
        ScreenTabs.DrawBatteryCorner(half, TabFonts(), 50);
        Assert.True(Black(full, new SKRectI(130, 0, 200, 30)) > Black(half, new SKRectI(130, 0, 200, 30)));  // a fuller cell is blacker
    }

    [Fact]
    public void Battery_follows_the_curve_of_a_lithium_cell()
    {
        Assert.Equal(0, ScreenTabs.BatteryPercent(3.2));
        Assert.Equal(50, ScreenTabs.BatteryPercent(3.7));
        Assert.Equal(65, ScreenTabs.BatteryPercent(3.8));
        Assert.Equal(90, ScreenTabs.BatteryPercent(4.1));
        Assert.InRange(ScreenTabs.BatteryPercent(3.725), 51, 64);
        // a cell on its charger reads 4.17-4.20 V: that is full, and shows as 100 in the corner's steps of five
        Assert.Equal(100, ScreenTabs.BatteryPercent(4.18));
        Assert.Equal(100, ScreenTabs.BatteryPercent(4.2));
        Assert.Equal(100, ScreenTabs.BatteryPercent(4.25));
        Assert.Equal(100, ScreenTabs.CornerPercent(ScreenTabs.BatteryPercent(4.17)));
        Assert.Equal(100, ScreenTabs.CornerPercent(ScreenTabs.BatteryPercent(4.2)));
        // never going down as the voltage goes up
        var last = -1;
        for (var v = 3.0; v <= 4.4; v += 0.01)
        {
            var now = ScreenTabs.BatteryPercent(v);
            Assert.True(now >= last, $"{v:0.00} V gave {now} after {last}");
            last = now;
        }
    }

    // ---- the icon: exactly mirror symmetric at every size, where the layout puts it

    private static (int Left, int Top, int W, int H) IconBox(BatteryLayout? layout, int screenWidth)
    {
        var s = Math.Clamp(layout?.Scale ?? 1, 1, BatteryLayout.MaxScale);
        return (layout?.X ?? screenWidth - 6 * s - BatteryLayout.BaseWidth * s, layout?.Y ?? 4, BatteryLayout.BaseWidth * s, BatteryLayout.BaseHeight * s);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 37)]
    [InlineData(1, 100)]
    [InlineData(2, 55)]
    [InlineData(3, 100)]
    [InlineData(4, 15)]
    public void The_battery_icon_is_mirror_symmetric_left_to_right_at_every_size(int scale, int percent)
    {
        using var bitmap = Blank();
        var layout = new BatteryLayout(true, 40, 30, scale);
        ScreenTabs.DrawBatteryCorner(bitmap, TabFonts(), percent, false, layout);
        var (left, top, w, h) = IconBox(layout, 200);
        for (var y = top; y < top + h; y++)
            for (var x = left; x < left + w; x++)
                Assert.Equal(bitmap.GetPixel(x, y).Red < 128, bitmap.GetPixel(left + w - 1 - (x - left), y).Red < 128);
        // something is there, and the nub is on top: the row above the shell has only the middle columns
        Assert.True(Black(bitmap, new SKRectI(left, top, left + w, top + h)) > 20 * scale);
    }

    [Fact]
    public void The_icon_goes_where_the_layout_says_at_the_size_it_says_and_an_old_layout_keeps_the_corner()
    {
        // no layout at all (an old screen): the top right corner at the base size
        using var old = Blank();
        ScreenTabs.DrawBatteryCorner(old, TabFonts(), 64, false, null);
        using var asDefault = Blank();
        ScreenTabs.DrawBatteryCorner(asDefault, TabFonts(), 64, false, new BatteryLayout());
        Assert.Equal(Bytes(old), Bytes(asDefault));
        Assert.True(Black(old, new SKRectI(184, 4, 194, 22)) > 30);  // 200 - 6 - 10 = 184

        // moved and magnified: the corner is empty, the new place holds it
        using var moved = Blank();
        ScreenTabs.DrawBatteryCorner(moved, TabFonts(), 64, false, new BatteryLayout(true, 20, 120, 3));
        Assert.Equal(0, Black(moved, new SKRectI(130, 0, 200, 30)));
        Assert.True(Black(moved, new SKRectI(20, 120, 50, 174)) > 100);   // 30 by 54 points, the shell is 3 points thick
        Assert.Equal(0, Black(moved, new SKRectI(20, 175, 200, 200)));   // nothing under it: the percent stands to its LEFT, not below

        // taken away
        using var off = Blank();
        ScreenTabs.DrawBatteryCorner(off, TabFonts(), 64, false, new BatteryLayout(false));
        Assert.Equal(0, Black(off, new SKRectI(0, 0, 200, 200)));
    }

    [Fact]
    public void A_charging_icon_has_a_bolt_where_the_percent_would_stand_and_no_percent()
    {
        using var plain = Blank();
        using var charging = Blank();
        ScreenTabs.DrawBatteryCorner(plain, TabFonts(), 80, false, null);
        ScreenTabs.DrawBatteryCorner(charging, TabFonts(), 80, true, null);
        var icon = new SKRectI(184, 4, 194, 22);
        Assert.Equal(Black(plain, icon), Black(charging, icon));           // the cell itself is the same
        var left = new SKRectI(150, 0, 183, 30);
        Assert.NotEqual(Bytes(plain), Bytes(charging));
        Assert.InRange(Black(charging, left), 20, 80);                      // a bolt of 9 by 15 points: not the text of the percent
        // the bolt is a column of ink from top to bottom, narrower than the text "80%"
        Assert.True(Black(charging, new SKRectI(172, 7, 182, 24)) > 25);
        Assert.Equal(0, Black(charging, new SKRectI(150, 0, 170, 30)));
    }

    private static byte[] Bytes(SKBitmap b) => b.Bytes;

    [Fact]
    public void The_percent_text_grows_with_the_icon_and_stays_beside_it()
    {
        using var big = Blank();
        ScreenTabs.DrawBatteryCorner(big, TabFonts(), 64, false, new BatteryLayout(true, 140, 10, 2));
        Assert.True(Black(big, new SKRectI(60, 10, 137, 50)) > 30);   // "65%" in 20 px letters to the left of the icon
        Assert.Equal(0, Black(big, new SKRectI(0, 60, 200, 200)));
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
        var sun = new SKRectI(150, 138, 240, 190);
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
    public void The_thermometer_is_a_tube_with_marks_and_stays_in_its_box()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(60, 60, SKColorType.Gray8, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            WeatherIcons.Thermometer(canvas, 8, 6, 44);
        }
        var width = WeatherIcons.ThermometerWidthAt(44);
        Assert.InRange(width, 28, 33);
        Assert.InRange(Black(bitmap, new SKRectI(0, 0, 60, 60)), 300, 900);
        Assert.Equal(0, Black(bitmap, new SKRectI(0, 0, 60, 5)));  // nothing above the tube
        Assert.Equal(0, Black(bitmap, new SKRectI(0, 51, 60, 60)));  // nor under the bulb
        Assert.Equal(0, Black(bitmap, new SKRectI(0, 0, 7, 60)));  // nor left of it
        Assert.Equal(0, Black(bitmap, new SKRectI((int)Math.Ceiling(8 + width) + 1, 0, 60, 60)));  // nor right of the marks
        Assert.True(Black(bitmap, new SKRectI(8 + (int)(width * 0.7f), 8, 8 + (int)width, 26)) > 20, "the marks are beside the tube");
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
        Assert.Equal(new[] { 8, 16, 24 }, DisplayFontCatalog.SizesOf(ScreenTabs.PixelFamily).Take(3));
        Assert.Equal(new[] { 16, 32, 48 }, DisplayFontCatalog.SizesOf("pixellari").Take(3));
        Assert.Contains(12, DisplayFontCatalog.SizesOf("roboto"));
        Assert.Contains(36, DisplayFontCatalog.SizesOf("roboto-serif"));
        Assert.Equal(DisplayFontCatalog.OutlineSizes, DisplayFontCatalog.SizesOf("my-uploaded-font"));
        Assert.Equal(DisplayFontCatalog.OutlineSizes, DisplayFontCatalog.SizesOf(null));
    }

    [Theory]
    [InlineData(ScreenTabs.Forecast)]
    [InlineData(ScreenTabs.Measure)]
    public void A_sleeping_panel_says_so_in_place_of_when_it_was_drawn(int tab)
    {
        var awake = Context();
        using var awakePicture = Draw(tab, awake);
        using var asleepPicture = Draw(tab, awake with { Asleep = true });
        var stamp = new SKRectI(300, 196, 416, 218);
        var (woke, slept) = (Black(awakePicture, stamp), Black(asleepPicture, stamp));
        Assert.True(woke > 0 && slept > 0);
        Assert.True(slept < woke, "\"спит\" is shorter than \"обновлено 23:58\"");
    }
}
