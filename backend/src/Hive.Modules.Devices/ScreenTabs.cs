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
    DateTimeOffset Now, Func<DateTimeOffset, DateTimeOffset> Local, int? Battery, WeatherForecast? Weather, IReadOnlyList<TabEvent> Events);

/// <summary>
/// The tabs of a panel with buttons (contracts/monitor-http.md, tabs): Common (a clock), Forecast (the weather now and the
/// next five days), Calendar (the month and the hive's recent events) and Measure (the person's own layout). Each ends in
/// the tab bar the panel finds by itself: the bottom <see cref="BarHeight"/> pixels, four equal cells, the shown tab inverted.
/// </summary>
public static class ScreenTabs
{
    public const int Common = 0, Forecast = 1, Calendar = 2, Measure = 3;
    public const int BarHeight = 22;
    public static readonly string[] Names = ["Часы", "Прогноз", "Календарь", "Измерения"];

    /// <summary>The families the tabs are written in: Anonymous Pro (Russian letters, clear digits), UnifrakturCook for the clock.</summary>
    public const string TextFamily = "anonymous-pro";
    public const string ClockFamily = "unifraktur-cook";

    // Written out: the hive's image may run without culture data.
    private static readonly string[] Weekdays = ["понедельник", "вторник", "среда", "четверг", "пятница", "суббота", "воскресенье"];
    private static readonly string[] Months = ["январь", "февраль", "март", "апрель", "май", "июнь", "июль", "август", "сентябрь", "октябрь", "ноябрь", "декабрь"];
    private static readonly string[] MonthsOf = ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];
    private static readonly string[] WeekdaysShort = ["пн", "вт", "ср", "чт", "пт", "сб", "вс"];
    private static readonly string[] MonthsShort = ["янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"];

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
    public static SKBitmap Draw(int tab, DisplayScreenDto screen, TabContext context, FontSet fonts, DisplayMessage items, SKBitmap? background)
    {
        var bitmap = tab == Measure
            ? DisplayRender.Render(items, screen, fonts, background)
            : new SKBitmap(new SKImageInfo(screen.Width, screen.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        if (tab != Measure)
            canvas.Clear(SKColors.White);
        var pen = new Pen(canvas, fonts, screen.Width, screen.Height - BarHeight);
        switch (tab)
        {
            case Common: DrawCommon(pen, context); break;
            case Forecast: DrawForecast(pen, context); break;
            case Calendar: DrawCalendar(pen, context); break;
        }
        DrawBattery(pen, context.Battery);
        DrawBar(pen, tab, screen.Height);
        return bitmap;
    }

    /// <summary>Drawing tools of one tab: ink, white ink, the text faces and the area above the bar.</summary>
    private sealed class Pen
    {
        public Pen(SKCanvas canvas, FontSet fonts, int width, int height)
        {
            Canvas = canvas;
            W = width;
            H = height;
            Mono = fonts.Resolve(TextFamily, false, false);
            MonoBold = fonts.Resolve(TextFamily, true, false);
            Clock = fonts.Resolve(ClockFamily, true, false);
            Fallback = fonts.Fallback;
        }

        public SKCanvas Canvas { get; }
        public int W { get; }
        public int H { get; }
        public FontFace Mono { get; }
        public FontFace MonoBold { get; }
        public FontFace Clock { get; }
        public FontFace Fallback { get; }
        public SKPaint Ink { get; } = new() { Color = SKColors.Black, IsAntialias = false, StrokeWidth = 1, Style = SKPaintStyle.Fill };
        public SKPaint Paper { get; } = new() { Color = SKColors.White, IsAntialias = false, Style = SKPaintStyle.Fill };
        public SKPaint Line(float width = 1) => new() { Color = SKColors.Black, IsAntialias = false, StrokeWidth = width, Style = SKPaintStyle.Stroke };

        /// <summary>Text of <paramref name="px"/> pixels, its top at <paramref name="top"/>; returns the height of its capitals.</summary>
        public float Text(string text, float x, float top, float px, string? align = null, FontFace? face = null, bool white = false) =>
            DisplayRender.Text(Canvas, white ? Paper : Ink, text, x, top, face ?? Mono, Fallback, px, false, align);

        public float Width(string text, float px, FontFace? face = null)
        {
            using var font = DisplayRender.MakeFont(face ?? Mono, px);
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

    private static void DrawBar(Pen p, int tab, int height)
    {
        var top = height - BarHeight;
        using var line = p.Line();
        p.Canvas.DrawRect(new SKRect(0, top, p.W, height), p.Paper);
        p.Canvas.DrawLine(0, top, p.W, top, line);
        var cell = p.W / (float)Names.Length;
        for (var i = 0; i < Names.Length; i++)
        {
            var left = i * cell;
            if (i == tab)
                p.Canvas.DrawRect(new SKRect(left, top, left + cell, height), p.Ink);
            else if (i > 0 && i != tab + 1)
                p.Canvas.DrawLine(left, top + 4, left, height - 4, line);
            p.Text(Names[i], left + cell / 2, top + 6, 13, "center", i == tab ? p.MonoBold : p.Mono, white: i == tab);
        }
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
        p.Text($"{pct}%", right - w - 3, top + 5, 10, "right");
    }

    /// <summary>The week as seven squares, Monday first: weekdays black, the weekend grey; today marked under (or around) its square.</summary>
    private static void DrawWeek(Pen p, float x, float y, float size, float gap, DateTimeOffset today, bool circle)
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
            if (circle)
            {
                using var ring = p.Line(2);
                p.Canvas.DrawCircle(r.MidX, r.MidY, size * 0.85f, ring);
            }
            else
            {
                using var mark = new SKPath();
                mark.MoveTo(r.MidX, r.Bottom + 3);
                mark.LineTo(r.MidX - 6, r.Bottom + 10);
                mark.LineTo(r.MidX + 6, r.Bottom + 10);
                mark.Close();
                p.Canvas.DrawPath(mark, p.Ink);
            }
        }
    }

    // ------------------------------------------------------------------ Common: the clock

    private static void DrawCommon(Pen p, TabContext c)
    {
        var now = c.Local(c.Now);
        DrawWeek(p, 10, 10, 18, 7, now, circle: false);
        var date = $"{now.Day} {MonthsShort[now.Month - 1]}";
        var dateRight = p.W - (c.Battery is null ? 10 : 50);
        var dateWidth = p.Width(date, 22);
        p.Text(date, dateRight, 10, 22, "right");
        using (var underline = p.Line(2))
            p.Canvas.DrawLine(dateRight - dateWidth, 37, dateRight, 37, underline);

        // the largest clock that fits between the week and the bar
        var time = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        float px = 200;
        while (px > 40 && (p.Width(time, px, p.Clock) > p.W - 20 || CapOf(p.Clock, px) > p.H - 70))
            px -= 4;
        var cap = CapOf(p.Clock, px);
        p.Text(time, p.W / 2f, 48 + (p.H - 48 - cap) / 2 - 6, px, "center", p.Clock);
        p.Text(Weekdays[DayIndex(now)], p.W - 8, p.H - 16, 12, "right");
    }

    private static float CapOf(FontFace face, float px)
    {
        using var font = DisplayRender.MakeFont(face, px);
        return font.Metrics.CapHeight > 0 ? font.Metrics.CapHeight : px * 0.7f;
    }

    // ------------------------------------------------------------------ Forecast: now and five days

    private static void DrawForecast(Pen p, TabContext c)
    {
        var now = c.Local(c.Now);
        var weather = c.Weather;
        p.Text($"{now.Day} {WeekdaysShort[DayIndex(now)]}", 6, 4, 13);
        p.Text(Months[now.Month - 1], 6, 20, 13);
        p.Text(now.ToString("HH:mm", CultureInfo.InvariantCulture), 6, 36, 13);
        if (weather?.Current is not { } current)
        {
            p.Text("Нет прогноза: укажите место", p.W / 2f, p.H / 2f - 10, 14, "center");
            p.Text("в настройках погоды Улья", p.W / 2f, p.H / 2f + 10, 14, "center");
            return;
        }
        WeatherIcons.Draw(p.Canvas, current.WeatherCode, 92, 4, 46);
        var temp = current.Temperature.ToString("0.0", CultureInfo.InvariantCulture) + "°";
        p.Text(temp, 222, 2, 38, "center");
        if (current.Pressure is { } hpa)
            p.Text($"Давл. {Math.Round(hpa * 0.750062).ToString(CultureInfo.InvariantCulture)} мм", 222, 44, 11, "center");
        var right = 340f;
        if (current.CloudCover is { } clouds)
            p.Text($"Обл. {clouds}%", right, 6, 12, "right");
        if (current.Humidity is { } humidity)
            p.Text($"Влаж. {Math.Round(humidity).ToString(CultureInfo.InvariantCulture)}%", right, 22, 12, "right");
        if (current.WindSpeed is { } wind)
        {
            var speed = wind.ToString("0.0", CultureInfo.InvariantCulture) + " м/с";
            p.Text(speed, right, 40, 12, "right");
            if (current.WindDirection is { } from)
                WindArrow(p, right - p.Width(speed, 12) - 12, 46, from);
        }
        using (var thick = p.Line(2))
            p.Canvas.DrawLine(0, 60, p.W, 60, thick);

        // five days from today: date, hours 3 9 15 21, the temperature line, rain and wind per six hours
        const float left = 20;
        var dayW = (p.W - left - 2) / 5f;
        var start = new DateTimeOffset(now.Date, now.Offset);
        var hours = weather.Hourly.Select(h => (At: c.Local(h.Time), Hour: h)).Where(h => h.At >= start && h.At < start.AddDays(5)).ToList();
        float gTop = 92, gBottom = 152, rainTop = 156, rainBottom = 184, windTop = 188;
        for (var d = 0; d < 5; d++)
        {
            var x = left + d * dayW;
            var day = start.AddDays(d);
            p.Text(day.ToString("dd/MM", CultureInfo.InvariantCulture), x + 4, 64, 13, null, d == 0 ? p.MonoBold : p.Mono);
            for (var s = 0; s < 4; s++)
                p.Text((3 + 6 * s).ToString(CultureInfo.InvariantCulture), x + dayW * (s + 0.5f) / 4, 80, 9, "center");
            if (d > 0)
                p.Dotted(x, 64, x, windTop + 12);
        }
        using (var thin = p.Line())
            p.Canvas.DrawLine(0, 90, p.W, 90, thin);
        if (hours.Count == 0)
            return;

        var (low, high, step) = Scale(hours.Min(h => h.Hour.Temperature), hours.Max(h => h.Hour.Temperature));
        float Y(double t) => gBottom - (float)((t - low) / (high - low)) * (gBottom - gTop);
        float X(DateTimeOffset at) => left + (float)((at - start).TotalHours / 24.0) * dayW;
        for (var t = low; t <= high; t += step)
        {
            p.Dotted(left, Y(t), p.W - 2, Y(t));
            p.Text(t.ToString(CultureInfo.InvariantCulture), left - 2, Y(t) - 4, 9, "right");
        }
        using (var curve = p.Line(2))
        using (var path = new SKPath())
        {
            path.MoveTo(X(hours[0].At), Y(hours[0].Hour.Temperature));
            foreach (var (at, hour) in hours.Skip(1))
                path.LineTo(X(at), Y(hour.Temperature));
            p.Canvas.DrawPath(path, curve);
        }
        // the warmest of each day above the line
        for (var d = 0; d < 5; d++)
        {
            var ofDay = hours.Where(h => h.At >= start.AddDays(d) && h.At < start.AddDays(d + 1)).ToList();
            if (ofDay.Count == 0)
                continue;
            var max = ofDay.MaxBy(h => h.Hour.Temperature);
            var label = Math.Round(max.Hour.Temperature).ToString("0", CultureInfo.InvariantCulture);
            p.Text(label, Math.Clamp(X(max.At), left + d * dayW + 8, left + (d + 1) * dayW - 8), Math.Max(gTop - 12, Y(max.Hour.Temperature) - 13), 10, "center");
        }

        WeatherIcons.Drop(p.Canvas, 8, rainTop + 8, 6);
        WeatherIcons.Wind(p.Canvas, 2, windTop + 2, 14);
        var slotW = dayW / 4;
        for (var slot = 0; slot < 20; slot++)
        {
            var from = start.AddHours(6 * slot);
            var of = hours.Where(h => h.At >= from && h.At < from.AddHours(6)).Select(h => h.Hour).ToList();
            if (of.Count == 0)
                continue;
            var x = left + slot * slotW;
            // grey: the chance of rain, black: how much (6 mm in six hours fills the row)
            if (of.Max(h => h.PrecipitationProbability) is { } chance && chance > 0)
                p.Grey(new SKRect(x + 3, rainBottom - (rainBottom - rainTop) * chance / 100f, x + slotW / 2, rainBottom));
            var mm = of.Sum(h => h.Precipitation);
            if (mm > 0.05)
                p.Canvas.DrawRect(new SKRect(x + slotW / 2, rainBottom - Math.Max(2, (float)Math.Min(1, mm / 6) * (rainBottom - rainTop)), x + slotW - 3, rainBottom), p.Ink);
            if (of.Max(h => h.WindSpeed) is { } windMax)
                p.Text(Math.Round(windMax).ToString("0", CultureInfo.InvariantCulture), x + slotW / 2, windTop + 3, 10, "center");
        }
        p.Text("обновлено " + now.ToString("HH:mm", CultureInfo.InvariantCulture), p.W - 4, p.H - 12, 9, "right");
    }

    /// <summary>An arrow the way the wind blows (it comes from <paramref name="fromDegrees"/>).</summary>
    private static void WindArrow(Pen p, float cx, float cy, int fromDegrees)
    {
        var to = (fromDegrees + 180) * Math.PI / 180;
        float dx = (float)Math.Sin(to), dy = (float)-Math.Cos(to);
        using var line = p.Line(2);
        p.Canvas.DrawLine(cx - dx * 7, cy - dy * 7, cx + dx * 7, cy + dy * 7, line);
        using var head = new SKPath();
        head.MoveTo(cx + dx * 9, cy + dy * 9);
        head.LineTo(cx + dx * 3 - dy * 4, cy + dy * 3 + dx * 4);
        head.LineTo(cx + dx * 3 + dy * 4, cy + dy * 3 - dx * 4);
        head.Close();
        p.Canvas.DrawPath(head, p.Ink);
    }

    // ------------------------------------------------------------------ Calendar: the month and the hive's news

    private static void DrawCalendar(Pen p, TabContext c)
    {
        var now = c.Local(c.Now);
        const float cell = 28, left = 6;
        string[] heads = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];
        for (var i = 0; i < 7; i++)
            p.Text(heads[i], left + cell * i + cell / 2, 8, 12, "center", i >= 5 ? p.MonoBold : p.Mono);
        p.Dotted(left, 26, left + cell * 7, 26);
        var first = new DateTime(now.Year, now.Month, 1);
        var gridStart = first.AddDays(-((int)first.DayOfWeek + 6) % 7);
        var rows = (int)Math.Ceiling(((first - gridStart).TotalDays + DateTime.DaysInMonth(now.Year, now.Month)) / 7);
        var rowH = Math.Min(30f, (p.H - 34) / (float)rows);
        for (var i = 0; i < rows * 7; i++)
        {
            var day = gridStart.AddDays(i);
            float x = left + cell * (i % 7) + cell / 2, y = 32 + rowH * (i / 7);
            var other = day.Month != now.Month;
            p.Text(day.Day.ToString(CultureInfo.InvariantCulture), x, y + 3, other ? 11 : 15, "center", i % 7 >= 5 && !other ? p.MonoBold : p.Mono);
            if (day.Date == now.Date)
            {
                using var ring = p.Line(2);
                p.Canvas.DrawCircle(x, y + 10, 13, ring);
            }
        }
        var divider = left + cell * 7 + 8;
        using (var line = p.Line(2))
            p.Canvas.DrawLine(divider, 6, divider, p.H - 6, line);

        var x0 = divider + 10;
        var dayText = now.Day.ToString(CultureInfo.InvariantCulture);
        p.Text(dayText, x0, 8, 54);
        p.Text(MonthsOf[now.Month - 1], x0 + p.Width(dayText, 54) + 8, 36, 16);
        DrawWeek(p, x0, 76, 12, 9, now, circle: true);
        float y0 = 106;
        if (c.Events.Count == 0)
            p.Text("Событий нет", x0, y0, 12);
        foreach (var e in c.Events.Take(4))
        {
            var at = c.Local(e.At);
            var line = at.ToString("dd/MM", CultureInfo.InvariantCulture) + " " + e.What;
            p.Text(line, x0, y0, 12);
            if (e.Where is { Length: > 0 } where)
                p.Text(Fit(p, where, p.W - x0 - p.Width(line + " ", 12) - 4, 10), x0 + p.Width(line + " ", 12), y0 + 2, 10);
            y0 += 22;
        }
    }

    private static string Fit(Pen p, string text, float width, float px)
    {
        if (p.Width(text, px) <= width)
            return text;
        while (text.Length > 1 && p.Width(text + "…", px) > width)
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

    /// <summary>A drop of radius <paramref name="r"/>, its round bottom at x, y.</summary>
    public static void Drop(SKCanvas canvas, float x, float y, float r)
    {
        using var path = new SKPath();
        path.MoveTo(x, y - r * 2.4f);
        path.LineTo(x - r, y - r * 0.2f);
        path.ArcTo(new SKRect(x - r, y - r, x + r, y + r), 180, -180, false);
        path.Close();
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
        canvas.DrawPath(path, ink);
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

    /// <summary>Three lines of wind, the longest in the middle.</summary>
    public static void Wind(SKCanvas canvas, float x, float y, float size)
    {
        using var line = new SKPaint { Color = SKColors.Black, IsAntialias = false, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
        canvas.DrawLine(x, y + size * 0.25f, x + size * 0.7f, y + size * 0.25f, line);
        canvas.DrawLine(x, y + size * 0.5f, x + size, y + size * 0.5f, line);
        canvas.DrawLine(x, y + size * 0.75f, x + size * 0.6f, y + size * 0.75f, line);
    }
}
