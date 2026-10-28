using System.Globalization;
using Hive.Contracts.Messages;
using Hive.Modules.Weather;
using SkiaSharp;

namespace Hive.Modules.Devices;

/// <summary>A recent event of the hive for the calendar tab: when, what, where.</summary>
public sealed record TabEvent(DateTimeOffset At, string What, string? Where);

/// <summary>
/// What the tabs show besides the person's own items. <see cref="Now"/> and the weather times are turned into the hive's time
/// zone by <see cref="Local"/>; <see cref="Battery"/> is percent, null when the panel cannot measure it.
/// </summary>
public sealed record TabContext(
    DateTimeOffset Now, Func<DateTimeOffset, DateTimeOffset> Local, int? Battery, WeatherForecast? Weather, IReadOnlyList<TabEvent> Events, bool Asleep = false);

/// <summary>
/// The tabs of a panel with buttons (contracts/monitor-http.md, tabs): Weather (now and the next days), Measure (the person's
/// own layout) and Calendar (the month and the hive's recent events). Each ends in the tab bar the panel finds by itself: the
/// bottom <see cref="BarHeight"/> pixels, three equal cells, the shown tab inverted. The panel may stand either way (the
/// person's rotation): the tabs lay themselves out in a landscape or a portrait screen.
/// </summary>
public static class ScreenTabs
{
    public const int Forecast = 0, Measure = 1, Calendar = 2;
    public const int BarHeight = 22;
    public static readonly string[] Names = ["Погода", "Измерения", "Календарь"];

    /// <summary>
    /// How wide the cells of the tab bar are, relative to each other: Weather is the shortest word and gets the least room.
    /// The panel learns it from X-Frame-Tab-Weights, since it inverts the cell under its cursor itself.
    /// </summary>
    public static readonly int[] TabWeights = [2, 3, 3];
    public static string TabWeightsHeader => string.Join(',', TabWeights);

    /// <summary>The left edges of the cells and the right edge of the last one, in a bar <paramref name="width"/> pixels wide.</summary>
    public static int[] CellEdges(int width)
    {
        var edges = new int[TabWeights.Length + 1];
        var total = TabWeights.Sum();
        var sum = 0;
        for (var i = 0; i < TabWeights.Length; i++)
        {
            edges[i] = width * sum / total;
            sum += TabWeights[i];
        }
        edges[^1] = width;
        return edges;
    }

    /// <summary>
    /// The families the tabs are written in: Roboto for text (bold for what stands out, light for scales), Roboto Serif for
    /// the bigger numbers, a pixel font for the 8 px small print.
    /// </summary>
    public const string TextFamily = "roboto";
    public const string LightFamily = "roboto-light";
    public const string SerifFamily = "roboto-serif";
    public const string PixelFamily = "public-pixel";
    public static readonly string[] Families = [TextFamily, LightFamily, SerifFamily, PixelFamily];

    // Written out: the hive's image may run without culture data.
    private static readonly string[] Weekdays = ["понедельник", "вторник", "среда", "четверг", "пятница", "суббота", "воскресенье"];
    private static readonly string[] Months = ["Январь", "Февраль", "Март", "Апрель", "Май", "Июнь", "Июль", "Август", "Сентябрь", "Октябрь", "Ноябрь", "Декабрь"];
    private static readonly string[] MonthsOf = ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];

    /// <summary>Monday first: 0 for Monday, 6 for Sunday.</summary>
    public static int DayIndex(DateTimeOffset day) => ((int)day.DayOfWeek + 6) % 7;

    /// <summary>A lithium cell from empty (3.30 V) to full (4.20 V), along its curve: most of the charge sits around 3.7-3.9 V.</summary>
    public static int BatteryPercent(double volts)
    {
        (double V, int Pct)[] curve = [(3.30, 0), (3.50, 5), (3.60, 10), (3.70, 25), (3.75, 40), (3.80, 50), (3.85, 60), (3.90, 70), (4.00, 82), (4.10, 92), (4.20, 100)];
        if (volts <= curve[0].V)
            return 0;
        for (var i = 1; i < curve.Length; i++)
            if (volts <= curve[i].V)
            {
                var (v0, p0) = curve[i - 1];
                var (v1, p1) = curve[i];
                return (int)Math.Round(p0 + (p1 - p0) * (volts - v0) / (v1 - v0));
            }
        return 100;
    }

    /// <summary>The scale of the temperature graph: lines every 5 degrees (2 when the days are even), at least two of them.</summary>
    public static (int Low, int High, int Step) Scale(double min, double max)
    {
        var step = max - min > 8 ? 5 : 2;
        var low = (int)Math.Floor(min / step) * step;
        var high = (int)Math.Ceiling(max / step) * step;
        if (high == low)
            high = low + step;
        return (low, high, step);
    }

    /// <summary>One tab as the person sees it (turned): the background and items for Measure, the hive's drawing for the others.</summary>
    public static SKBitmap Draw(int tab, DisplayScreenDto screen, TabContext context, FontSet fonts, DisplayMessage items, SKBitmap? background,
        IReadOnlyDictionary<int, SKBitmap>? images = null)
    {
        var bitmap = tab == Measure
            ? DisplayRender.Render(items, screen, fonts, background, images)
            : new SKBitmap(new SKImageInfo(screen.Width, screen.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        if (tab != Measure)
            canvas.Clear(SKColors.White);
        var pen = new Pen(canvas, fonts, screen.Width, screen.Height - BarHeight) { Asleep = context.Asleep };
        switch (tab)
        {
            case Measure: Stamp(pen, context.Local(context.Now)); break;
            case Forecast: DrawForecast(pen, context); break;
            case Calendar: DrawCalendar(pen, context); break;
        }
        DrawBattery(pen, context.Battery);
        DrawBar(pen, tab, screen.Height);
        return bitmap;
    }

    /// <summary>When the picture was drawn, in the pixel font at the bottom right (the same on Weather and Measure).</summary>
    private static void Stamp(Pen p, DateTimeOffset now) =>
        p.Text(p.Asleep ? "спит" : "обновлено " + now.ToString("HH:mm", CultureInfo.InvariantCulture), p.W - 4, p.H - 10, 8, "right", p.Pixel);

    /// <summary>Drawing tools of one tab: ink, white ink, the text faces and the area above the bar.</summary>
    private sealed class Pen
    {
        public Pen(SKCanvas canvas, FontSet fonts, int width, int height)
        {
            Canvas = canvas;
            W = width;
            H = height;
            Regular = fonts.Resolve(TextFamily, false, false);
            Bold = fonts.Resolve(TextFamily, true, false);
            Light = fonts.Resolve(LightFamily, false, false);
            LightBold = fonts.Resolve(LightFamily, true, false);
            Serif = fonts.Resolve(SerifFamily, false, false);
            Pixel = fonts.Resolve(PixelFamily, false, false);
            Fallback = fonts.Fallback;
        }

        public SKCanvas Canvas { get; }
        public int W { get; }
        public int H { get; }
        /// <summary>The panel is put to sleep: the stamp says so instead of when the picture was drawn.</summary>
        public bool Asleep { get; init; }
        public bool Portrait => W < H;
        public FontFace Regular { get; }
        public FontFace Bold { get; }
        public FontFace Light { get; }
        /// <summary>Roboto Light has no bold of its own: this one is the light face made heavier.</summary>
        public FontFace LightBold { get; }
        public FontFace Serif { get; }
        public FontFace Pixel { get; }
        public FontFace Fallback { get; }
        public SKPaint Ink { get; } = new() { Color = SKColors.Black, IsAntialias = false, StrokeWidth = 1, Style = SKPaintStyle.Fill };
        public SKPaint Paper { get; } = new() { Color = SKColors.White, IsAntialias = false, Style = SKPaintStyle.Fill };
        public SKPaint Line(float width = 1) => new() { Color = SKColors.Black, IsAntialias = false, StrokeWidth = width, Style = SKPaintStyle.Stroke };

        /// <summary>Text of <paramref name="px"/> pixels, its top at <paramref name="top"/>; returns the height of its capitals.</summary>
        public float Text(string text, float x, float top, float px, string? align = null, FontFace? face = null, bool white = false) =>
            DisplayRender.Text(Canvas, white ? Paper : Ink, text, x, top, face ?? Regular, Fallback, px, false, align);

        public float Width(string text, float px, FontFace? face = null)
        {
            using var font = DisplayRender.MakeFont(face ?? Regular, px);
            return font.MeasureText(text);
        }

        /// <summary>Grey on a black-and-white screen: every other pixel.</summary>
        public void Grey(SKRect r)
        {
            for (var y = (int)r.Top; y < (int)r.Bottom; y++)
                for (var x = (int)r.Left; x < (int)r.Right; x++)
                    if ((x + y) % 2 == 0)
                        Canvas.DrawPoint(x, y, Ink);
        }

        public void Dotted(float x0, float y0, float x1, float y1)
        {
            using var dots = Line();
            dots.PathEffect = SKPathEffect.CreateDash([1, 3], 0);
            Canvas.DrawLine(x0, y0, x1, y1, dots);
        }
    }

    /// <summary>The tab bar: the cells of <see cref="TabWeights"/>, the shown one black; a name that does not fit its cell is written smaller.</summary>
    private static void DrawBar(Pen p, int tab, int height)
    {
        var top = height - BarHeight;
        using var line = p.Line();
        p.Canvas.DrawRect(new SKRect(0, top, p.W, height), p.Paper);
        p.Canvas.DrawLine(0, top, p.W, top, line);
        var edges = CellEdges(p.W);
        var px = 14f;
        while (px > 10 && Enumerable.Range(0, Names.Length).Any(i => p.Width(Names[i], px, p.LightBold) > edges[i + 1] - edges[i] - 6))
            px -= 1;
        for (var i = 0; i < Names.Length; i++)
        {
            float left = edges[i], right = edges[i + 1];
            if (i == tab)
                p.Canvas.DrawRect(new SKRect(left, top, right, height), p.Ink);
            else if (i > 0 && i != tab + 1)
                p.Canvas.DrawLine(left, top + 4, left, height - 4, line);
            var cap = CapOf(p.LightBold, px);
            p.Text(Names[i], (left + right) / 2, top + (BarHeight - cap) / 2 + 1, px, "center", p.LightBold, white: i == tab);
        }
    }

    private static float CapOf(FontFace face, float px)
    {
        using var font = DisplayRender.MakeFont(face, px);
        return font.Metrics.CapHeight > 0 ? font.Metrics.CapHeight : px * 0.7f;
    }

    /// <summary>A small battery standing upright in the top right corner, the percent to its left. Nothing when unknown.</summary>
    private static void DrawBattery(Pen p, int? percent)
    {
        if (percent is not { } pct)
            return;
        float right = p.W - 6, top = 4, w = 10, h = 18;
        using var line = p.Line();
        p.Canvas.DrawRect(new SKRect(right - w + 3, top, right - 3, top + 2), p.Ink);
        p.Canvas.DrawRect(new SKRect(right - w, top + 2, right, top + h), line);
        var fill = (h - 4) * Math.Clamp(pct, 0, 100) / 100f;
        p.Canvas.DrawRect(new SKRect(right - w + 2, top + h - 2 - fill, right - 2, top + h - 2), p.Ink);
        // standing, the date takes the whole top line: the percent goes under the battery
        if (p.Portrait)
            p.Text($"{pct}%", right - w / 2, top + h + 3, 9, "center", p.Light);
        else
            p.Text($"{pct}%", right - w - 3, top + 5, 10, "right", p.Light);
    }

    /// <summary>The week as seven squares, Monday first: weekdays black, the weekend grey; today circled.</summary>
    private static void DrawWeek(Pen p, float x, float y, float size, float gap, DateTimeOffset today)
    {
        var index = DayIndex(today);
        for (var i = 0; i < 7; i++)
        {
            var r = new SKRect(x + i * (size + gap), y, x + i * (size + gap) + size, y + size);
            if (i < 5)
                p.Canvas.DrawRect(r, p.Ink);
            else
            {
                p.Grey(r);
                using var edge = p.Line();
                p.Canvas.DrawRect(r, edge);
            }
            if (i != index)
                continue;
            using var ring = p.Line(2);
            p.Canvas.DrawCircle(r.MidX, r.MidY, size * 0.85f, ring);
        }
    }

    // ------------------------------------------------------------------ Weather: now and the next days

    private static void DrawForecast(Pen p, TabContext c)
    {
        var now = c.Local(c.Now);
        var weather = c.Weather;
        if (p.Portrait)
        {
            // standing: the whole date in one line, a line under it
            var date = $"{now.Day} {MonthsOf[now.Month - 1]} {Weekdays[DayIndex(now)]}";
            p.Text(date, 6, 6, p.Width(date, 14, p.Bold) <= p.W - 34 ? 14 : 12, null, p.Bold);
            using var under = p.Line();
            p.Canvas.DrawLine(4, 26, p.W - 28, 26, under);
        }
        else
        {
            // the date, big: the day and month, the weekday under it
            p.Text($"{now.Day} {MonthsOf[now.Month - 1]}", 6, 6, 16, null, p.Bold);
            p.Text(Weekdays[DayIndex(now)], 6, 28, 14);
        }
        if (weather?.Current is not { } current)
        {
            p.Text("Нет прогноза: укажите место", p.W / 2f, p.H / 2f - 10, 14, "center");
            p.Text("в настройках погоды Улья", p.W / 2f, p.H / 2f + 10, 14, "center");
            return;
        }
        float headBottom;
        if (p.Portrait)
            headBottom = DrawNowStanding(p, c, current, now);
        else
        {
            WeatherIcons.Draw(p.Canvas, current.WeatherCode, 104, 4, 46);
            p.Text(Degrees(current.Temperature), 228, 7, 36, "center", p.Serif);
            if (current.Pressure is { } hpa)
                p.Text(Pressure(hpa), 228, 46, 12, "center", p.Serif);
            var details = Details(current);
            for (var i = 0; i < details.Count; i++)
                p.Text(details[i], 352, 6 + 17 * i, 12, "right");
            if (current.WindSpeed is not null && current.WindDirection is { } from)
                WindArrow(p, 352 - p.Width(details[^1], 12) - 10, 6 + 17 * (details.Count - 1) + 6, from);
            headBottom = 60;
        }
        using (var thick = p.Line(2))
            p.Canvas.DrawLine(0, headBottom, p.W, headBottom, thick);

        Stamp(p, now);

        // the time axis runs from now to the end of the day after tomorrow: what is left of today and two whole days. A day
        // that has little room left (today, late) goes without its date or its hours rather than squeezed in.
        const int ahead = 2;
        const float left = 22;
        var today = new DateTimeOffset(now.Date, now.Offset);
        var end = today.AddDays(1 + ahead);
        var axisW = p.W - left - 2;
        float X(DateTimeOffset at) => left + (float)((at - now).TotalHours / (end - now).TotalHours) * axisW;
        var points = new List<(DateTimeOffset At, double Temperature)> { (now, current.Temperature) };
        points.AddRange(weather.Hourly.Select(h => (At: c.Local(h.Time), h.Temperature)).Where(h => h.At > now && h.At < end));
        var hours = weather.Hourly.Select(h => (At: c.Local(h.Time), Hour: h)).Where(h => h.At > now.AddHours(-1) && h.At < end).ToList();
        var y0 = headBottom + 4;
        float hoursTop = y0 + 16, hoursBottom = y0 + 28;
        // the graph sits under the hours with room for the warmest of each day; it may run over the rain, not the hours
        var rest = p.H - hoursBottom - 14;
        float gTop = hoursBottom + 12, gBottom = hoursBottom + rest * 0.52f, rainTop = gBottom + 4, rainBottom = hoursBottom + rest * 0.8f, windTop = rainBottom + 5;
        for (var d = 0; d <= ahead; d++)
        {
            var dayStart = today.AddDays(d);
            float colLeft = X(dayStart > now ? dayStart : now), colRight = X(dayStart.AddDays(1));
            var label = dayStart.ToString("dd/MM", CultureInfo.InvariantCulture);
            var face = dayStart.Date == now.Date ? p.Bold : p.Serif;
            if (colRight - colLeft >= p.Width(label, 12, face) + 8)
                p.Text(label, colLeft + 4, y0, 12, null, face);
            for (var s = 0; s < 4; s++)
            {
                var middle = dayStart.AddHours(6 * s + 3);
                if (middle > now && X(middle) - 6 >= colLeft)
                    p.Text((3 + 6 * s).ToString(CultureInfo.InvariantCulture), X(middle), hoursTop, 10, "center", p.Light);
            }
            if (d > 0)
                p.Dotted(colLeft, y0, colLeft, windTop + 12);
        }
        using (var thin = p.Line())
            p.Canvas.DrawLine(0, hoursBottom + 1, p.W, hoursBottom + 1, thin);

        var (low, high, step) = Scale(points.Min(h => h.Temperature), points.Max(h => h.Temperature));
        float Y(double t) => gBottom - (float)((t - low) / (high - low)) * (gBottom - gTop);
        for (var t = low; t <= high; t += step)
        {
            p.Dotted(left, Y(t), p.W - 2, Y(t));
            p.Text(t.ToString(CultureInfo.InvariantCulture), left - 3, Y(t) - 4, 10, "right", p.Light);
        }
        using (var curve = p.Line(2))
        using (var path = new SKPath())
        {
            path.MoveTo(X(points[0].At), Y(points[0].Temperature));
            foreach (var (at, temperature) in points.Skip(1))
                path.LineTo(X(at), Y(temperature));
            p.Canvas.DrawPath(path, curve);
        }
        // the warmest of each day above the line, never up on the hours
        for (var d = 0; d <= ahead; d++)
        {
            var dayStart = today.AddDays(d);
            float colLeft = X(dayStart > now ? dayStart : now), colRight = X(dayStart.AddDays(1));
            var ofDay = points.Where(h => h.At >= dayStart && h.At < dayStart.AddDays(1)).ToList();
            if (ofDay.Count == 0 || colRight - colLeft < 28)
                continue;
            var max = ofDay.MaxBy(h => h.Temperature);
            var text = Math.Round(max.Temperature).ToString("0", CultureInfo.InvariantCulture);
            p.Text(text, Math.Clamp(X(max.At), colLeft + 8, colRight - 8), Math.Max(hoursBottom + 3, Y(max.Temperature) - 12), 10, "center");
        }

        WeatherIcons.Drops(p.Canvas, 2, rainTop + 2, 14);
        WeatherIcons.Wind(p.Canvas, 2, windTop - 1, 16);
        for (var slot = 0; slot < (1 + ahead) * 4; slot++)
        {
            var slotStart = today.AddHours(6 * slot);
            var slotEnd = slotStart.AddHours(6);
            if (slotEnd <= now)
                continue;
            // the slot that is going on is only what is left of it
            float x = X(slotStart > now ? slotStart : now), slotW = X(slotEnd) - x;
            var of = hours.Where(h => h.At >= slotStart && h.At < slotEnd).Select(h => h.Hour).ToList();
            if (of.Count == 0 || slotW < 10)
                continue;
            // grey: the chance of rain, black: how much (6 mm in six hours fills the row)
            if (of.Max(h => h.PrecipitationProbability) is { } chance && chance > 0)
                p.Grey(new SKRect(x + 3, rainBottom - (rainBottom - rainTop) * chance / 100f, x + slotW / 2, rainBottom));
            var mm = of.Sum(h => h.Precipitation);
            if (mm > 0.05)
                p.Canvas.DrawRect(new SKRect(x + slotW / 2, rainBottom - Math.Max(2, (float)Math.Min(1, mm / 6) * (rainBottom - rainTop)), x + slotW - 3, rainBottom), p.Ink);
            if (of.Max(h => h.WindSpeed) is { } windMax)
                p.Text(Math.Round(windMax).ToString("0", CultureInfo.InvariantCulture), x + slotW / 2, windTop + 3, 10, "center");
        }
    }

    /// <summary>
    /// The weather now, standing (the person's design): thermometer, temperature and picture; how warm it feels and the
    /// weather in words; humidity, clouds, pressure and wind in a column, sunrise and sunset beside them. Returns its bottom.
    /// </summary>
    private static float DrawNowStanding(Pen p, TabContext c, WeatherCurrentDto current, DateTimeOffset now)
    {
        WeatherIcons.Thermometer(p.Canvas, 8, 36, 44);
        p.Text(Degrees(current.Temperature), 30, 44, 36, null, p.Serif);
        WeatherIcons.Draw(p.Canvas, current.WeatherCode, p.W - 76, 34, 70);
        // how warm it feels, bigger; beside the picture, not under it
        if (current.Apparent is { } apparent)
        {
            var feels = "Ощущается: " + Degrees(apparent);
            var feelsPx = 16f;
            while (feelsPx > 12 && p.Width(feels, feelsPx, p.Bold) > p.W - 84 - 6)
                feelsPx--;
            p.Text(feels, 6, 86, feelsPx, null, p.Bold);
        }
        // the weather in words stands on the dotted line, which the words erase
        const float lineY = 116;
        var words = Fit(p, WeatherIcons.Describe(current.WeatherCode), p.W - 40, 14, p.Bold);
        var wordsW = p.Width(words, 14, p.Bold);
        var wordsLeft = (p.W - wordsW) / 2;
        p.Dotted(4, lineY, wordsLeft - 6, lineY);
        p.Dotted(wordsLeft + wordsW + 6, lineY, p.W - 4, lineY);
        p.Text(words, p.W / 2f, lineY - CapOf(p.Bold, 14) / 2, 14, "center", p.Bold);

        const float rowsTop = 126;
        var rows = new List<(string Label, string Value)>();
        if (current.Humidity is { } humidity)
            rows.Add(("Влажн", Math.Round(humidity).ToString(CultureInfo.InvariantCulture) + "%"));
        if (current.CloudCover is { } clouds)
            rows.Add(("Облачн", clouds.ToString(CultureInfo.InvariantCulture) + "%"));
        if (current.Pressure is { } hpa)
            rows.Add(("Давлен", Math.Round(hpa * 0.750062).ToString(CultureInfo.InvariantCulture) + " мм"));
        if (current.WindSpeed is { } wind)
            rows.Add(("Ветер", wind.ToString("0.0", CultureInfo.InvariantCulture) + " м/с"));
        for (var i = 0; i < rows.Count; i++)
        {
            var y = rowsTop + 18 * i;
            p.Text(rows[i].Label, 6, y, 13);
            p.Text(":", 54, y, 13);
            p.Text(rows[i].Value, 62, y, 13);
            if (rows[i].Label == "Ветер" && current.WindDirection is { } from)
                WindArrow(p, 62 + p.Width(rows[i].Value, 13) + 12, y + 5, from);
        }

        // the sun today, by the hive's clock
        var today = DateOnly.FromDateTime(now.Date);
        if (c.Weather?.Daily.FirstOrDefault(d => d.Date == today) is { } day)
        {
            float rise = p.W - 84, set = p.W - 36;
            if (day.Sunrise is { } up)
            {
                WeatherIcons.SunOnHorizon(p.Canvas, rise - 16, rowsTop + 18, 32, rising: true);
                p.Text(c.Local(up).ToString("H:mm", CultureInfo.InvariantCulture), rise, rowsTop + 48, 13, "center");
            }
            if (day.Sunset is { } down)
            {
                WeatherIcons.SunOnHorizon(p.Canvas, set - 16, rowsTop + 18, 32, rising: false);
                p.Text(c.Local(down).ToString("H:mm", CultureInfo.InvariantCulture), set, rowsTop + 48, 13, "center");
            }
        }
        return Math.Max(190, rowsTop + 18 * rows.Count + 2);
    }

    private static string Degrees(double t) => t.ToString("0.0", CultureInfo.InvariantCulture) + "°";

    private static string Pressure(double hpa) => $"Давл. {Math.Round(hpa * 0.750062).ToString(CultureInfo.InvariantCulture)} мм";

    /// <summary>Clouds, humidity and the wind (always last), as far as the weather says them.</summary>
    private static List<string> Details(WeatherCurrentDto current)
    {
        var lines = new List<string>();
        if (current.CloudCover is { } clouds)
            lines.Add($"Обл. {clouds}%");
        if (current.Humidity is { } humidity)
            lines.Add($"Влаж. {Math.Round(humidity).ToString(CultureInfo.InvariantCulture)}%");
        if (current.WindSpeed is { } wind)
            lines.Add(wind.ToString("0.0", CultureInfo.InvariantCulture) + " м/с");
        return lines;
    }

    /// <summary>An arrow the way the wind blows (it comes from <paramref name="fromDegrees"/>).</summary>
    private static void WindArrow(Pen p, float cx, float cy, int fromDegrees)
    {
        var to = (fromDegrees + 180) * Math.PI / 180;
        float dx = (float)Math.Sin(to), dy = (float)-Math.Cos(to);
        using var line = p.Line(2);
        p.Canvas.DrawLine(cx - dx * 6, cy - dy * 6, cx + dx * 6, cy + dy * 6, line);
        using var head = new SKPath();
        head.MoveTo(cx + dx * 8, cy + dy * 8);
        head.LineTo(cx + dx * 2 - dy * 4, cy + dy * 2 + dx * 4);
        head.LineTo(cx + dx * 2 + dy * 4, cy + dy * 2 - dx * 4);
        head.Close();
        p.Canvas.DrawPath(head, p.Ink);
    }

    // ------------------------------------------------------------------ Calendar: the month and the hive's news

    /// <summary>
    /// The month on the left and today with the news on the right; standing, the month above and the news under it, as many
    /// as fit down to the tabs.
    /// </summary>
    private static void DrawCalendar(Pen p, TabContext c)
    {
        var now = c.Local(c.Now);
        var cell = p.Portrait ? (p.W - 12) / 7f : 28f;
        const float left = 6;
        var monthBottom = p.Portrait ? p.H * 0.52f : p.H;
        // standing, the top line is the month's name (the battery stands at its right end), the weeks under it
        var top0 = 0f;
        if (p.Portrait)
        {
            p.Text($"{Months[now.Month - 1]} {now.Year}", left, 4, 18, null, p.Bold);
            top0 = 32;
        }
        string[] heads = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];
        for (var i = 0; i < 7; i++)
            p.Text(heads[i], left + cell * i + cell / 2, top0 + 8, 12, "center", i >= 5 ? p.Bold : p.Regular);
        p.Dotted(left, top0 + 26, left + cell * 7, top0 + 26);
        var first = new DateTime(now.Year, now.Month, 1);
        var gridStart = first.AddDays(-((int)first.DayOfWeek + 6) % 7);
        var rows = (int)Math.Ceiling(((first - gridStart).TotalDays + DateTime.DaysInMonth(now.Year, now.Month)) / 7);
        var rowH = Math.Min(30f, (monthBottom - top0 - 34) / rows);
        for (var i = 0; i < rows * 7; i++)
        {
            var day = gridStart.AddDays(i);
            float x = left + cell * (i % 7) + cell / 2, y = top0 + 32 + rowH * (i / 7);
            var other = day.Month != now.Month;
            p.Text(day.Day.ToString(CultureInfo.InvariantCulture), x, y + 3, other ? 10 : 14, "center", i % 7 >= 5 && !other ? p.Bold : other ? p.Light : p.Regular);
            if (day.Date == now.Date)
            {
                using var ring = p.Line(2);
                p.Canvas.DrawCircle(x, y + 9, Math.Min(13, rowH / 2), ring);
            }
        }

        float x0, y0;
        using (var line = p.Line(2))
        {
            if (p.Portrait)
            {
                // standing, today is already circled in the month: the rest of the screen is the news
                var gridBottom = top0 + 32 + rowH * rows;
                p.Canvas.DrawLine(6, gridBottom + 2, p.W - 6, gridBottom + 2, line);
                (x0, y0) = (8, gridBottom + 10);
            }
            else
            {
                var divider = left + cell * 7 + 8;
                p.Canvas.DrawLine(divider, 6, divider, p.H - 6, line);
                x0 = divider + 10;
                const float top = 8;
                var dayText = now.Day.ToString(CultureInfo.InvariantCulture);
                p.Text(dayText, x0, top, 48, null, p.Serif);
                var dayW = p.Width(dayText, 48, p.Serif);
                p.Text(MonthsOf[now.Month - 1], x0 + dayW + 8, top + 4, 16, null, p.Bold);
                p.Text(Weekdays[DayIndex(now)], x0 + dayW + 8, top + 22, 14);
                DrawWeek(p, x0 + 2, top + 46, 12, 9, now);
                y0 = top + 72;
            }
        }
        if (c.Events.Count == 0)
            p.Text("Событий нет", x0, y0, 12);
        const float lineH = 19;
        // a line of 12 px with its tails is 16 px tall from its top: the last one ends above the tabs
        var room = (int)((p.H - 2 - y0 - 16) / lineH) + 1;
        foreach (var e in c.Events.Take(Math.Max(0, room)))
        {
            var at = c.Local(e.At);
            var date = at.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture) + " ";
            p.Text(date, x0, y0, 12, null, p.Serif);
            var x = x0 + p.Width(date, 12, p.Serif);
            var what = Fit(p, e.What, p.W - x - 4, 12, p.Bold);
            p.Text(what, x, y0, 12, null, p.Bold);
            x += p.Width(what + " ", 12, p.Bold);
            if (e.Where is { Length: > 0 } where && p.W - x - 4 > 12)
                p.Text(Fit(p, where, p.W - x - 4, 12), x, y0, 12);
            y0 += lineH;
        }
    }

    /// <summary>
    /// The news for the calendar, newest first, each once: the same thing from the same place in the same minute (three motion
    /// frames of one camera) is one line.
    /// </summary>
    public static IReadOnlyList<TabEvent> Distinct(IEnumerable<TabEvent> events, Func<DateTimeOffset, DateTimeOffset> local) =>
        events.OrderByDescending(e => e.At)
            .DistinctBy(e => (local(e.At).ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture), e.What, e.Where))
            .ToList();

    private static string Fit(Pen p, string text, float width, float px, FontFace? face = null)
    {
        if (p.Width(text, px, face) <= width)
            return text;
        while (text.Length > 1 && p.Width(text + "…", px, face) > width)
            text = text[..^1];
        return text + "…";
    }
}

/// <summary>Thin line pictures of the weather, by the WMO code Open-Meteo gives: sun, clouds, rain, snow, thunder, fog.</summary>
public static class WeatherIcons
{
    public enum Kind { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Snow, Thunder }

    public static Kind Of(int code) => code switch
    {
        0 or 1 => Kind.Clear,
        2 => Kind.PartlyCloudy,
        3 => Kind.Cloudy,
        45 or 48 => Kind.Fog,
        >= 51 and <= 57 => Kind.Drizzle,
        >= 61 and <= 67 or >= 80 and <= 82 => Kind.Rain,
        >= 71 and <= 77 or 85 or 86 => Kind.Snow,
        >= 95 => Kind.Thunder,
        _ => Kind.Cloudy,
    };

    private static SKPaint Stroke(float size) => new() { Color = SKColors.Black, IsAntialias = false, StrokeWidth = Math.Max(1.5f, size / 22), Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };

    /// <summary>The picture for a code in a square of <paramref name="size"/> pixels at x, y.</summary>
    public static void Draw(SKCanvas canvas, int code, float x, float y, float size)
    {
        var kind = Of(code);
        switch (kind)
        {
            case Kind.Clear:
                Sun(canvas, x + size / 2, y + size / 2, size * 0.2f, size);
                return;
            case Kind.PartlyCloudy:
                Sun(canvas, x + size * 0.66f, y + size * 0.32f, size * 0.15f, size);
                break;
        }
        Cloud(canvas, x, y + (kind == Kind.Cloudy || kind == Kind.PartlyCloudy ? size * 0.15f : 0), size);
        var below = y + size * 0.72f;
        switch (kind)
        {
            case Kind.Fog:
                using (var line = Stroke(size))
                    for (var i = 0; i < 2; i++)
                        canvas.DrawLine(x + size * 0.15f, below + i * size * 0.12f, x + size * 0.85f, below + i * size * 0.12f, line);
                break;
            case Kind.Drizzle:
            case Kind.Rain:
                var count = kind == Kind.Rain ? 3 : 2;
                for (var i = 0; i < count; i++)
                    Drop(canvas, x + size * (0.3f + 0.2f * i + (count == 2 ? 0.1f : 0)), below + (i % 2) * size * 0.08f, size * 0.07f);
                break;
            case Kind.Snow:
                for (var i = 0; i < 3; i++)
                    Flake(canvas, x + size * (0.3f + 0.2f * i), below + (i % 2) * size * 0.1f + size * 0.05f, size * 0.07f);
                break;
            case Kind.Thunder:
                using (var bolt = new SKPath())
                {
                    bolt.MoveTo(x + size * 0.52f, below - size * 0.02f);
                    bolt.LineTo(x + size * 0.42f, below + size * 0.14f);
                    bolt.LineTo(x + size * 0.52f, below + size * 0.14f);
                    bolt.LineTo(x + size * 0.44f, below + size * 0.28f);
                    using var line = Stroke(size);
                    canvas.DrawPath(bolt, line);
                }
                break;
        }
    }

    private static void Sun(SKCanvas canvas, float cx, float cy, float r, float size)
    {
        using var line = Stroke(size);
        canvas.DrawCircle(cx, cy, r, line);
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
            canvas.DrawLine(cx + dx * r * 1.45f, cy + dy * r * 1.45f, cx + dx * r * 1.95f, cy + dy * r * 1.95f, line);
        }
    }

    /// <summary>A cloud: three bumps on a flat bottom, white inside (it covers a sun behind it).</summary>
    public static void Cloud(SKCanvas canvas, float x, float y, float size)
    {
        using var a = new SKPath();
        a.AddCircle(x + size * 0.32f, y + size * 0.52f, size * 0.16f);
        using var b = new SKPath();
        b.AddCircle(x + size * 0.52f, y + size * 0.4f, size * 0.2f);
        using var d = new SKPath();
        d.AddCircle(x + size * 0.72f, y + size * 0.52f, size * 0.15f);
        using var e = new SKPath();
        e.AddRect(new SKRect(x + size * 0.32f, y + size * 0.45f, x + size * 0.72f, y + size * 0.67f));
        using var ab = a.Op(b, SKPathOp.Union)!;
        using var abd = ab.Op(d, SKPathOp.Union)!;
        using var cloud = abd.Op(e, SKPathOp.Union)!;
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = false, Style = SKPaintStyle.Fill };
        canvas.DrawPath(cloud, white);
        using var line = Stroke(size);
        canvas.DrawPath(cloud, line);
    }

    /// <summary>The weather in words (Russian), by its WMO code.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Ясно",
        1 => "Почти ясно",
        2 => "Переменная облачность",
        3 => "Пасмурно",
        45 or 48 => "Туман",
        51 or 53 or 55 => "Морось",
        56 or 57 => "Ледяная морось",
        61 => "Небольшой дождь",
        63 => "Дождь",
        65 => "Сильный дождь",
        66 or 67 => "Ледяной дождь",
        71 => "Небольшой снег",
        73 => "Снег",
        75 => "Сильный снег",
        77 => "Снежная крупа",
        80 => "Небольшие ливни",
        81 => "Ливни",
        82 => "Сильные ливни",
        85 or 86 => "Снегопад",
        95 => "Гроза",
        96 or 99 => "Гроза с градом",
        _ => "",
    };

    /// <summary>A thermometer <paramref name="height"/> tall at x, y: a tube with marks, the bulb and its column filled.</summary>
    public static void Thermometer(SKCanvas canvas, float x, float y, float height)
    {
        var r = height * 0.16f;
        var tube = height * 0.09f;
        var cx = x + r;
        using var line = new SKPaint { Color = SKColors.Black, IsAntialias = false, StrokeWidth = Math.Max(1.5f, height / 22), Style = SKPaintStyle.Stroke };
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
        var bulbY = y + height - r;
        canvas.DrawRoundRect(new SKRect(cx - tube, y, cx + tube, bulbY - r * 0.6f), tube, tube, line);
        canvas.DrawCircle(cx, bulbY, r, ink);
        canvas.DrawRect(new SKRect(cx - tube * 0.45f, y + height * 0.35f, cx + tube * 0.45f, bulbY), ink);
        for (var i = 0; i < 3; i++)
            canvas.DrawLine(cx + tube + 2, y + height * (0.18f + 0.14f * i), cx + tube + 5, y + height * (0.18f + 0.14f * i), line);
    }

    /// <summary>
    /// Half a sun on the horizon (three lines under it), <paramref name="width"/> wide at x, y: rising drawn in outline,
    /// setting filled.
    /// </summary>
    public static void SunOnHorizon(SKCanvas canvas, float x, float y, float width, bool rising)
    {
        var size = width;
        using var line = new SKPaint
        {
            Color = SKColors.Black, IsAntialias = false, StrokeWidth = Math.Max(1.5f, size / 20), Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round,
        };
        float cx = x + size / 2, horizon = y + size * 0.5f, r = size * 0.2f;
        using (var half = new SKPath())
        {
            half.AddArc(new SKRect(cx - r, horizon - r, cx + r, horizon + r), 180, 180);
            if (rising)
                canvas.DrawPath(half, line);
            else
            {
                half.Close();
                using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
                canvas.DrawPath(half, ink);
            }
        }
        for (var i = 0; i < 5; i++)
        {
            var a = Math.PI + i * Math.PI / 4;
            float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
            canvas.DrawLine(cx + dx * r * 1.4f, horizon + dy * r * 1.4f, cx + dx * r * 1.9f, horizon + dy * r * 1.9f, line);
        }
        canvas.DrawLine(x, horizon + 1, x + size, horizon + 1, line);
        canvas.DrawLine(x + size * 0.15f, horizon + size * 0.13f, x + size * 0.85f, horizon + size * 0.13f, line);
        canvas.DrawLine(x + size * 0.3f, horizon + size * 0.25f, x + size * 0.7f, horizon + size * 0.25f, line);
    }

    /// <summary>A drop of radius <paramref name="r"/>, its round bottom at x, y.</summary>
    public static void Drop(SKCanvas canvas, float x, float y, float r) => Tear(canvas, x, y - r * 2.4f, x, y, r);

    /// <summary>A teardrop: its point at (tx, ty), its round end a circle of <paramref name="r"/> around (cx, cy).</summary>
    public static void Tear(SKCanvas canvas, float tx, float ty, float cx, float cy, float r)
    {
        // the two lines from the point touch the circle where they meet its radius at a right angle
        float dx = tx - cx, dy = ty - cy;
        var d = MathF.Sqrt(dx * dx + dy * dy);
        var toPoint = MathF.Atan2(dy, dx);
        var spread = MathF.Acos(Math.Clamp(r / d, -1, 1));
        var a1 = toPoint + spread;
        using var path = new SKPath();
        path.MoveTo(tx, ty);
        path.LineTo(cx + r * MathF.Cos(a1), cy + r * MathF.Sin(a1));
        // around the far side of the circle, from one touching point to the other
        var sweep = 360f - 2 * spread * 180 / MathF.PI;
        path.ArcTo(new SKRect(cx - r, cy - r, cx + r, cy + r), a1 * 180 / MathF.PI, sweep, false);
        path.LineTo(tx, ty);
        path.Close();
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
        canvas.DrawPath(path, ink);
    }

    /// <summary>Two drops slanting across each other (the rain row), in a box <paramref name="size"/> tall at x, y.</summary>
    public static void Drops(SKCanvas canvas, float x, float y, float size)
    {
        var w = size * 0.625f;
        Tear(canvas, x + w * 0.37f, y + size * 0.49f, x + w * 0.70f, y + size * 0.21f, w * 0.22f);
        Tear(canvas, x + w * 0.17f, y + size * 0.42f, x + w * 0.62f, y + size * 0.75f, w * 0.21f);
    }

    private static void Flake(SKCanvas canvas, float cx, float cy, float r)
    {
        using var line = new SKPaint { Color = SKColors.Black, IsAntialias = false, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke };
        for (var i = 0; i < 3; i++)
        {
            var a = i * Math.PI / 3;
            float dx = (float)Math.Cos(a) * r, dy = (float)Math.Sin(a) * r;
            canvas.DrawLine(cx - dx, cy - dy, cx + dx, cy + dy, line);
        }
    }

    /// <summary>
    /// The wind: three strokes blowing to the right, the upper one curling up at its end, the lower one curling down, the
    /// middle one the longest; in a square of <paramref name="size"/> at x, y.
    /// </summary>
    public static void Wind(SKCanvas canvas, float x, float y, float size)
    {
        using var line = new SKPaint
        {
            Color = SKColors.Black, IsAntialias = false, StrokeWidth = Math.Max(1.5f, size / 9), Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round,
        };
        float S(float f) => f * size;
        var r = S(0.13f);
        // upper: a dash, then a stroke that curls up and back over itself
        canvas.DrawLine(x + S(0.04f), y + S(0.38f), x + S(0.14f), y + S(0.38f), line);
        using (var upper = new SKPath())
        {
            upper.MoveTo(x + S(0.26f), y + S(0.38f));
            upper.LineTo(x + S(0.66f), y + S(0.38f));
            upper.ArcTo(new SKRect(x + S(0.66f) - r, y + S(0.38f) - 2 * r, x + S(0.66f) + r, y + S(0.38f)), 90, -270, false);
            canvas.DrawPath(upper, line);
        }
        // middle: the longest
        canvas.DrawLine(x + S(0.04f), y + S(0.56f), x + S(0.86f), y + S(0.56f), line);
        // lower: a stroke that curls down
        using (var lower = new SKPath())
        {
            lower.MoveTo(x + S(0.30f), y + S(0.74f));
            lower.LineTo(x + S(0.60f), y + S(0.74f));
            lower.ArcTo(new SKRect(x + S(0.60f) - r, y + S(0.74f), x + S(0.60f) + r, y + S(0.74f) + 2 * r), -90, 270, false);
            canvas.DrawPath(lower, line);
        }
    }
}
