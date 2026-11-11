using System.Globalization;
using Hive.Contracts.Messages;
using Hive.Modules.Weather;
using SkiaSharp;

namespace Hive.Modules.Devices;

/// <summary>The weather tab: now and the next days.</summary>
public static partial class ScreenTabs
{
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
        p.Text(Degrees(current.Temperature), 8 + WeatherIcons.ThermometerWidthAt(44) + 4, 44, 36, null, p.Serif);
        WeatherIcons.Draw(p.Canvas, current.WeatherCode, p.W - 76, 34, 70);
        // how warm it feels, bigger; beside the picture, not under it
        if (current.Apparent is { } apparent)
        {
            var feels = "Ощущается: " + Degrees(apparent);
            var feelsPx = 16f;
            while (feelsPx > 12 && p.Width(feels, feelsPx, p.BookItalic) > p.W - 84 - 6)
                feelsPx--;
            p.Text(feels, 6, 86, feelsPx, null, p.BookItalic);
        }
        // the weather in words stands on the right end of the dotted line, which the words erase
        const float lineY = 111;
        var words = Fit(p, WeatherIcons.Describe(current.WeatherCode), p.W - 40, 14, p.BookItalic);
        var wordsLeft = p.W - 4 - p.Width(words, 14, p.BookItalic);
        p.Dotted(4, lineY, wordsLeft - 6, lineY);
        p.Text(words, p.W - 4, lineY - CapOf(p.BookItalic, 14) / 2, 14, "right", p.BookItalic);

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
        // the labels share one column: the colon and the values follow the widest of them
        var colon = 6 + rows.Select(r => p.Width(r.Label, 14, p.Book)).DefaultIfEmpty(0).Max() + 2;
        var valueLeft = colon + p.Width(":", 14, p.Book) + 5;
        for (var i = 0; i < rows.Count; i++)
        {
            var y = rowsTop + 18 * i;
            p.Text(rows[i].Label, 6, y, 14, null, p.Book);
            p.Text(":", colon, y, 14, null, p.Book);
            p.Text(rows[i].Value, valueLeft, y, 14, null, p.Book);
            if (rows[i].Label == "Ветер" && current.WindDirection is { } from)
                WindArrow(p, valueLeft + p.Width(rows[i].Value, 14, p.Book) + 12, y + 5, from);
        }

        // the sun today, by the hive's clock
        var today = DateOnly.FromDateTime(now.Date);
        if (c.Weather?.Daily.FirstOrDefault(d => d.Date == today) is { } day)
        {
            float rise = p.W - 76, set = p.W - 30;
            if (day.Sunrise is { } up)
            {
                WeatherIcons.SunOnHorizon(p.Canvas, rise - 16, rowsTop + 18, 32, rising: true);
                p.Text(c.Local(up).ToString("H:mm", CultureInfo.InvariantCulture), rise, rowsTop + 48, 14, "center", p.Book);
            }
            if (day.Sunset is { } down)
            {
                WeatherIcons.SunOnHorizon(p.Canvas, set - 16, rowsTop + 18, 32, rising: false);
                p.Text(c.Local(down).ToString("H:mm", CultureInfo.InvariantCulture), set, rowsTop + 48, 14, "center", p.Book);
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
}
