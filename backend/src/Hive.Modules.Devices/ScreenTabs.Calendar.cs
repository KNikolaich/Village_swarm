using System.Globalization;
using Hive.Contracts.Messages;
using Hive.Modules.Weather;
using SkiaSharp;

namespace Hive.Modules.Devices;

/// <summary>The calendar tab: the month and the hive's news.</summary>
public static partial class ScreenTabs
{
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
}
